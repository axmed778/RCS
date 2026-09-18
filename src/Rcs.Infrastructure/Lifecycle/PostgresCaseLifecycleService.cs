using Rcs.Application.Common;
using Rcs.Application.Lifecycle;
using Rcs.Domain.Authorization;
using Rcs.Domain.Progress;
using Rcs.Domain.Vocabulary;
using Rcs.Domain.Workflow;
using Rcs.Infrastructure.Audit;
using Rcs.Infrastructure.Cases;
using Rcs.Infrastructure.Commands;
using Rcs.Infrastructure.Persistence;

namespace Rcs.Infrastructure.Lifecycle;

/// <summary>
/// The end of the case: the final result (WORKFLOW.md §8), closure (§9) and reopening (§10). Like every command,
/// each one takes the case lock first (ADR-019) so the guards read a case nobody is changing underneath them.
/// </summary>
internal sealed class PostgresCaseLifecycleService(CommandRunner runner, ProgressEvaluator progress) : ICaseLifecycleService
{
    /// <summary>The reason code of an authorised closure over a failing guard (WORKFLOW.md §9.4).</summary>
    private const string ClosureOverrideReason = "CLOSURE_GUARD_OVERRIDE";

    private const string ReopenReason = "REOPENED";

    // ----------------------------------------------------------- final result

    public Task<CommandResult<Guid>> DraftFinalResultAsync(ActorContext actor, DraftFinalResultCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        return runner.RunAsync(actor, "final_result.draft", command.OperationId, command.CaseId, async (scope, ct) =>
        {
            var caseRow = await CaseSql.LoadAsync(scope.UnitOfWork, command.CaseId, ct);
            if (caseRow is null)
            {
                return NotFound("case");
            }

            var resultId = scope.NewId();
            if (scope.Refuse(BusinessAction.DraftFinalResult, await RelationshipAsync(scope, caseRow, ct), AuditEntityTypes.FinalResult, resultId, caseRow.Id) is { } refused)
            {
                return refused;
            }

            var summary = Blank(command.Summary);
            if (FinalResultRules.CanDraft(caseRow.State, summary) is { IsAllowed: false } rule)
            {
                return Rule(rule);
            }

            var decisionTypeId = await scope.UnitOfWork.Command("SELECT id FROM rcs.decision_type WHERE code = @code AND is_active")
                .With("code", command.DecisionTypeCode ?? string.Empty).ScalarAsync<Guid?>(ct);
            if (decisionTypeId is null)
            {
                return Invalid("validation.required", nameof(command.DecisionTypeCode));
            }

            // A replacement may only name the case's currently ISSUED result (§8.6). It is verified again at issue,
            // because the result in force can change between drafting and issuing.
            if (command.SupersedesFinalResultId is { } supersedesId)
            {
                var inForce = await LifecycleSql.IssuedFinalResultIdAsync(scope.UnitOfWork, caseRow.Id, ct);
                if (inForce != supersedesId)
                {
                    return Invalid("final_result.supersedes_not_in_force", nameof(command.SupersedesFinalResultId));
                }
            }

            var resultNumber = await LifecycleSql.NextResultNumberAsync(scope.UnitOfWork, caseRow.CaseNumber, caseRow.Id, ct);
            await scope.UnitOfWork.Command("""
                    INSERT INTO rcs.final_result (id, case_id, result_number, decision_type_id, summary, reasoning,
                                                  supersedes_final_result_id, status, created_by_user_id)
                    VALUES (@id, @case, @number, @decision_type, @summary, @reasoning, @supersedes, 'DRAFT', @actor)
                    """)
                .With("id", resultId)
                .With("case", caseRow.Id)
                .With("number", resultNumber)
                .With("decision_type", decisionTypeId)
                .With("summary", summary)
                .With("reasoning", Blank(command.Reasoning))
                .With("supersedes", command.SupersedesFinalResultId)
                .With("actor", scope.Actor.UserId)
                .ExecuteAsync(ct);

            await scope.AuditAsync(new AuditEntry(AuditActionCodes.Create, AuditEntityTypes.FinalResult, resultId, 1, caseRow.Id,
                After: new
                {
                    result_number = resultNumber,
                    decision_type = command.DecisionTypeCode,
                    status = "DRAFT",
                    supersedes_final_result_id = command.SupersedesFinalResultId,
                }), ct);

            return CommandResult<Guid>.Success(resultId);
        }, cancellationToken);
    }

    public Task<CommandResult<Guid>> IssueFinalResultAsync(ActorContext actor, IssueFinalResultCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        return runner.RunAsync(actor, "final_result.issue", operationId: null, command.CaseId, async (scope, ct) =>
        {
            var loaded = await LoadResultAsync(scope, command.CaseId, command.FinalResultId, BusinessAction.IssueFinalResult, ct);
            if (loaded.Failure is { } failure)
            {
                return failure;
            }

            var (caseRow, result) = (loaded.Case!, loaded.Result!);
            var overrideNote = Blank(command.ReadinessOverrideNote);
            var ready = await LifecycleSql.IsReadyForFinalResultAsync(
                scope.UnitOfWork, caseRow, progress, scope.Now, result.SupersedesFinalResultId, ct);
            // D4 reads the real placements: ACTIVE FINAL_RESULT_DOCUMENT links, each pinned to one exact version
            // (DOCUMENT_MODEL.md §4.7). A later version of the document can never change what this decision relied on.
            var resultDocuments = await scope.UnitOfWork.Command("""
                    SELECT count(*) FROM rcs.document_link
                    WHERE final_result_id = @id AND status = 'ACTIVE' AND document_version_id IS NOT NULL
                      AND document_link_role_id = (SELECT id FROM rcs.document_link_role WHERE code = 'FINAL_RESULT_DOCUMENT')
                    """)
                .With("id", result.Id).ScalarAsync<long>(ct);
            var documentOverrideNote = Blank(command.DocumentOverrideNote);
            if (FinalResultRules.CanIssue(result.Status, ready, overrideNote, (int)resultDocuments, documentOverrideNote) is { IsAllowed: false } rule)
            {
                return Rule(rule);
            }

            var documentOverridden = resultDocuments == 0;

            // §8.6: retire the result being replaced FIRST, so "at most one ISSUED" holds at every statement.
            if (result.SupersedesFinalResultId is { } supersedesId)
            {
                var inForce = await LifecycleSql.IssuedFinalResultIdAsync(scope.UnitOfWork, caseRow.Id, ct);
                if (inForce != supersedesId)
                {
                    // Revoked or superseded meanwhile. Refused rather than quietly issued as a first result: the
                    // draft asserts it replaces a specific decision, and that assertion is no longer true.
                    return Rule(RuleCheck.Fail("final_result.supersedes_not_in_force"));
                }

                await SupersedeAsync(scope, supersedesId, caseRow.Id, ct);
            }

            var version = await scope.UnitOfWork.Command("""
                    UPDATE rcs.final_result
                    SET status = 'ISSUED',
                        decided_at = COALESCE(decided_at, @now), decided_by_user_id = COALESCE(decided_by_user_id, @actor),
                        approved_at = @now, approved_by_user_id = @actor, issued_at = @now,
                        updated_at = @now, updated_by_user_id = @actor, row_version = row_version + 1
                    WHERE id = @id AND row_version = @row_version AND status = 'DRAFT'
                    RETURNING row_version
                    """)
                .With("now", scope.Now)
                .With("actor", scope.Actor.UserId)
                .With("id", result.Id)
                .With("row_version", command.RowVersion)
                .ScalarAsync<int?>(ct);
            if (version is null)
            {
                return Conflict();
            }

            await scope.AuditAsync(new AuditEntry(AuditActionCodes.StateChange, AuditEntityTypes.FinalResult, result.Id, version, caseRow.Id,
                Before: new { status = "DRAFT" },
                After: new
                {
                    status = "ISSUED",
                    approved_by_user_id = scope.Actor.UserId,
                    issued_at = scope.Now,
                    readiness_overridden = !ready,
                    result_documents = resultDocuments,
                    document_guard_overridden = documentOverridden,
                },
                ReasonNote: string.Join(" · ", new[] { ready ? null : "D1: " + overrideNote, documentOverridden ? "D4: " + documentOverrideNote : null }
                    .Where(note => note is not null)) is { Length: > 0 } notes ? notes : null), ct);

            return CommandResult<Guid>.Success(result.Id);
        }, cancellationToken);
    }

    public Task<CommandResult<Guid>> RevokeFinalResultAsync(ActorContext actor, RevokeFinalResultCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        return runner.RunAsync(actor, "final_result.revoke", operationId: null, command.CaseId, async (scope, ct) =>
        {
            var loaded = await LoadResultAsync(scope, command.CaseId, command.FinalResultId, BusinessAction.RevokeFinalResult, ct);
            if (loaded.Failure is { } failure)
            {
                return failure;
            }

            var (caseRow, result) = (loaded.Case!, loaded.Result!);
            var reason = Blank(command.Reason);
            if (FinalResultRules.CanRevoke(result.Status, reason) is { IsAllowed: false } rule)
            {
                return Rule(rule);
            }

            var version = await scope.UnitOfWork.Command("""
                    UPDATE rcs.final_result
                    SET status = 'REVOKED', revoked_at = @now, revoked_by_user_id = @actor, revocation_reason_note = @reason,
                        updated_at = @now, updated_by_user_id = @actor, row_version = row_version + 1
                    WHERE id = @id AND row_version = @row_version AND status = 'ISSUED'
                    RETURNING row_version
                    """)
                .With("now", scope.Now)
                .With("actor", scope.Actor.UserId)
                .With("reason", reason)
                .With("id", result.Id)
                .With("row_version", command.RowVersion)
                .ScalarAsync<int?>(ct);
            if (version is null)
            {
                return Conflict();
            }

            await scope.AuditAsync(new AuditEntry(AuditActionCodes.StateChange, AuditEntityTypes.FinalResult, result.Id, version, caseRow.Id,
                Before: new { status = "ISSUED" }, After: new { status = "REVOKED", revoked_at = scope.Now }, ReasonNote: reason), ct);

            return CommandResult<Guid>.Success(result.Id);
        }, cancellationToken);
    }

    // ----------------------------------------------------------- closure

    public Task<CommandResult<Guid>> CloseCaseAsync(ActorContext actor, CloseCaseCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        return runner.RunAsync(actor, "case.close", operationId: null, command.CaseId, async (scope, ct) =>
        {
            var caseRow = await CaseSql.LoadAsync(scope.UnitOfWork, command.CaseId, ct);
            if (caseRow is null)
            {
                return NotFound("case");
            }

            var relationship = await RelationshipAsync(scope, caseRow, ct);
            if (scope.Refuse(BusinessAction.CloseCase, relationship, AuditEntityTypes.Case, caseRow.Id, caseRow.Id) is { } refused)
            {
                return refused;
            }

            if (CaseClosureRules.CanAttemptClosure(caseRow.State) is { IsAllowed: false } state)
            {
                return Rule(state);
            }

            var closureType = await LifecycleSql.ClosureTypeAsync(scope.UnitOfWork, command.ClosureTypeCode ?? string.Empty, ct);
            if (closureType is null)
            {
                return Invalid("validation.required", nameof(command.ClosureTypeCode));
            }

            var (facts, requirements, _) = await LifecycleSql.ClosureFactsAsync(scope.UnitOfWork, caseRow, closureType.ProducesDecision, ct);
            var guards = CaseClosureRules.Evaluate(facts);
            var failing = guards.Where(guard => !guard.Passed).ToArray();
            var overrideNote = Blank(command.OverrideNote);

            if (failing.Length > 0)
            {
                // Overriding is the Head's act alone, and the refusal is audited as a denial like any other.
                if (scope.Refuse(BusinessAction.OverrideClosureGuard, relationship, AuditEntityTypes.Case, caseRow.Id, caseRow.Id) is { } notPermitted)
                {
                    return notPermitted;
                }

                if (overrideNote is null)
                {
                    return Rule(RuleCheck.Fail("case.closure_override_reason_required"));
                }
            }

            // OB-4: closing over open non-blocking requirements is allowed, but only as a deliberate confirmation —
            // and it changes nothing about them. They stay OPEN on the closed case (§9.2.1 obligations 1 and 2).
            var unresolvedNonBlocking = CaseClosureRules
                .UnresolvedNonBlocking(requirements, requirement => new RequirementFacts(requirement.Status, requirement.IsBlocking))
                .ToArray();
            if (unresolvedNonBlocking.Length > 0 && !command.AcknowledgeUnresolvedNonBlocking)
            {
                return Rule(RuleCheck.Fail("case.unresolved_non_blocking_not_acknowledged"));
            }

            var note = Blank(command.Note);
            var version = await scope.UnitOfWork.Command("""
                    UPDATE rcs.case_record
                    SET lifecycle_state = 'CLOSED', closed_at = @now, closed_by_user_id = @actor,
                        closure_type_id = @closure_type, closure_note = @note,
                        updated_at = @now, updated_by_user_id = @actor, row_version = row_version + 1
                    WHERE id = @id AND row_version = @row_version
                    RETURNING row_version
                    """)
                .With("now", scope.Now)
                .With("actor", scope.Actor.UserId)
                .With("closure_type", closureType.Id)
                .With("note", note ?? overrideNote)
                .With("id", caseRow.Id)
                .With("row_version", command.RowVersion)
                .ScalarAsync<int?>(ct);
            if (version is null)
            {
                return Conflict();
            }

            var reasonCode = failing.Length > 0 ? ClosureOverrideReason : null;
            var stateNote = failing.Length > 0
                ? $"{string.Join(", ", failing.Select(guard => guard.Code))}: {overrideNote}"
                : note;

            await RecordStateChangeAsync(scope, caseRow, CaseLifecycleState.Closed, reasonCode, stateNote, ct);
            await scope.AuditAsync(new AuditEntry(AuditActionCodes.StateChange, AuditEntityTypes.Case, caseRow.Id, version, caseRow.Id,
                Before: new { lifecycle_state = caseRow.State.ToCode() },
                After: new
                {
                    lifecycle_state = "CLOSED",
                    closure_type = closureType.Code,
                    closed_at = scope.Now,
                    overridden_guards = failing.Select(guard => guard.Code).ToArray(),
                    unresolved_non_blocking = unresolvedNonBlocking.Length,
                },
                ReasonNote: stateNote), ct);

            return CommandResult<Guid>.Success(caseRow.Id);
        }, cancellationToken);
    }

    public Task<CommandResult<Guid>> ReopenCaseAsync(ActorContext actor, ReopenCaseCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        return runner.RunAsync(actor, "case.reopen", operationId: null, command.CaseId, async (scope, ct) =>
        {
            var caseRow = await CaseSql.LoadAsync(scope.UnitOfWork, command.CaseId, ct);
            if (caseRow is null)
            {
                return NotFound("case");
            }

            if (scope.Refuse(BusinessAction.ReopenCase, await RelationshipAsync(scope, caseRow, ct), AuditEntityTypes.Case, caseRow.Id, caseRow.Id) is { } refused)
            {
                return refused;
            }

            var reason = Blank(command.Reason);
            if (CaseClosureRules.CanReopen(caseRow.State, reason) is { IsAllowed: false } rule)
            {
                return Rule(rule);
            }

            // §10.1: closure metadata is retained, not cleared — the case shows it was closed and reopened later.
            // The ISSUED result stays in force; a changed outcome is a replacement (§8.6), not a side effect of this.
            var version = await scope.UnitOfWork.Command("""
                    UPDATE rcs.case_record
                    SET lifecycle_state = 'ACTIVE', updated_at = @now, updated_by_user_id = @actor, row_version = row_version + 1
                    WHERE id = @id AND row_version = @row_version AND lifecycle_state = 'CLOSED'
                    RETURNING row_version
                    """)
                .With("now", scope.Now)
                .With("actor", scope.Actor.UserId)
                .With("id", caseRow.Id)
                .With("row_version", command.RowVersion)
                .ScalarAsync<int?>(ct);
            if (version is null)
            {
                return Conflict();
            }

            await RecordStateChangeAsync(scope, caseRow, CaseLifecycleState.Active, ReopenReason, reason, ct);
            await scope.AuditAsync(new AuditEntry(AuditActionCodes.StateChange, AuditEntityTypes.Case, caseRow.Id, version, caseRow.Id,
                Before: new { lifecycle_state = "CLOSED" }, After: new { lifecycle_state = "ACTIVE" }, ReasonNote: reason), ct);

            return CommandResult<Guid>.Success(caseRow.Id);
        }, cancellationToken);
    }

    // ---------------------------------------------------------------- plumbing

    /// <summary>F3 — the replaced result is retired as a system consequence of issuing its replacement.</summary>
    private static async Task SupersedeAsync(CommandScope scope, Guid supersededId, Guid caseId, CancellationToken cancellationToken)
    {
        var version = await scope.UnitOfWork.Command("""
                UPDATE rcs.final_result
                SET status = 'SUPERSEDED', updated_at = @now, updated_by_user_id = @actor, row_version = row_version + 1
                WHERE id = @id AND status = 'ISSUED'
                RETURNING row_version
                """)
            .With("now", scope.Now)
            .With("actor", scope.Actor.UserId)
            .With("id", supersededId)
            .ScalarAsync<int?>(cancellationToken)
            ?? throw new InvalidOperationException("The superseded result changed inside the case lock.");

        await scope.AuditAsync(new AuditEntry(AuditActionCodes.StateChange, AuditEntityTypes.FinalResult, supersededId, version, caseId,
            Before: new { status = "ISSUED" }, After: new { status = "SUPERSEDED" }, ActorKind: ActorKind.System), cancellationToken);
    }

    private static Task RecordStateChangeAsync(CommandScope scope, CaseRow caseRow, CaseLifecycleState to, string? reasonCode, string? note, CancellationToken cancellationToken) =>
        scope.UnitOfWork.Command("""
                INSERT INTO rcs.case_state_change (id, case_id, from_state, to_state, reason_code, note, occurred_at, actor_user_id)
                VALUES (@id, @case, @from, @to, @reason_code, @note, @now, @actor)
                """)
            .With("id", scope.NewId())
            .With("case", caseRow.Id)
            .With("from", caseRow.State.ToCode())
            .With("to", to.ToCode())
            .With("reason_code", reasonCode)
            .With("note", note)
            .With("now", scope.Now)
            .With("actor", scope.Actor.UserId)
            .ExecuteAsync(cancellationToken);

    private static async Task<(CaseRow? Case, FinalResultRow? Result, CommandResult<Guid>? Failure)> LoadResultAsync(
        CommandScope scope, Guid caseId, Guid finalResultId, BusinessAction action, CancellationToken cancellationToken)
    {
        var caseRow = await CaseSql.LoadAsync(scope.UnitOfWork, caseId, cancellationToken);
        if (caseRow is null)
        {
            return (null, null, NotFound("case"));
        }

        var result = await LifecycleSql.LoadAsync(scope.UnitOfWork, finalResultId, cancellationToken);
        if (result is null || result.CaseId != caseRow.Id)
        {
            return (null, null, NotFound("final_result"));
        }

        var relationship = await RelationshipAsync(scope, caseRow, cancellationToken);
        return scope.Refuse(action, relationship, AuditEntityTypes.FinalResult, result.Id, caseRow.Id) is { } refused
            ? (null, null, refused)
            : (caseRow, result, null);
    }

    private static async Task<CaseRelationship> RelationshipAsync(CommandScope scope, CaseRow caseRow, CancellationToken cancellationToken) =>
        new(caseRow.IsRestricted, await CaseSql.ActorIsAssignedAsync(scope.UnitOfWork, caseRow.Id, scope.Actor.UserId, scope.Now, cancellationToken));

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static CommandResult<Guid> NotFound(string what) => CommandResult<Guid>.Failure(CommandErrorKind.NotFound, $"notfound.{what}");

    private static CommandResult<Guid> Rule(RuleCheck check) => CommandResult<Guid>.Failure(CommandErrorKind.RuleViolation, check.ViolationCode!);

    private static CommandResult<Guid> Invalid(string code, string field) => CommandResult<Guid>.Failure(CommandErrorKind.Validation, code, field);

    private static CommandResult<Guid> Conflict() => CommandResult<Guid>.Failure(CommandErrorKind.Conflict, "concurrency.changed_elsewhere");
}
