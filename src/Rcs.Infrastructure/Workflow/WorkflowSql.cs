using Npgsql;
using Rcs.Domain.Vocabulary;
using Rcs.Domain.Workflow;
using Rcs.Infrastructure.Audit;
using Rcs.Infrastructure.Cases;
using Rcs.Infrastructure.Commands;
using Rcs.Infrastructure.Persistence;

namespace Rcs.Infrastructure.Workflow;

internal sealed record RequestRow(Guid Id, Guid CaseId, RequestStatus Status, Guid TargetOrganizationId, Guid? DispatchCorrespondenceId, int RowVersion);

internal sealed record RequirementRow(Guid Id, Guid CaseId, RequirementStatus Status, bool IsBlocking, Guid? SourceResponseId, Guid CreatedByUserId, int RowVersion);

internal static class RequestSql
{
    public static Task<RequestRow?> LoadAsync(PostgresUnitOfWork unitOfWork, Guid requestId, CancellationToken cancellationToken) =>
        unitOfWork.Command("SELECT id, case_id, status, target_organization_id, dispatch_correspondence_id, row_version FROM rcs.request WHERE id = @id")
            .With("id", requestId)
            .SingleOrDefaultAsync(
                reader => new RequestRow(
                    reader.Uuid("id"),
                    reader.Uuid("case_id"),
                    VocabularyCodes.FromCode<RequestStatus>(reader.Text("status")),
                    reader.Uuid("target_organization_id"),
                    reader.UuidOrNull("dispatch_correspondence_id"),
                    reader.Int("row_version")),
                cancellationToken);

    public static async Task<IReadOnlyCollection<ResponseFacts>> ResponseFactsAsync(PostgresUnitOfWork unitOfWork, Guid requestId, CancellationToken cancellationToken) =>
        await unitOfWork.Command("""
                SELECT p.status, p.is_conclusive, o.code AS outcome
                FROM rcs.response AS p JOIN rcs.response_outcome AS o ON o.id = p.response_outcome_id
                WHERE p.request_id = @request
                """)
            .With("request", requestId)
            .ListAsync(reader => new ResponseFacts(VocabularyCodes.FromCode<ResponseStatus>(reader.Text("status")), reader.Bool("is_conclusive"), reader.Text("outcome")), cancellationToken);

    /// <summary>The requirements raised by this request's own responses — the ones its closure rule reads.</summary>
    public static async Task<IReadOnlyCollection<RequirementFacts>> RequirementFactsAsync(PostgresUnitOfWork unitOfWork, Guid requestId, CancellationToken cancellationToken) =>
        await unitOfWork.Command("""
                SELECT q.status, q.is_blocking
                FROM rcs.requirement AS q JOIN rcs.response AS p ON p.id = q.source_response_id
                WHERE p.request_id = @request
                """)
            .With("request", requestId)
            .ListAsync(reader => new RequirementFacts(VocabularyCodes.FromCode<RequirementStatus>(reader.Text("status")), reader.Bool("is_blocking")), cancellationToken);
}

internal static class RequirementSql
{
    public static Task<RequirementRow?> LoadAsync(PostgresUnitOfWork unitOfWork, Guid requirementId, CancellationToken cancellationToken) =>
        unitOfWork.Command("SELECT id, case_id, status, is_blocking, source_response_id, created_by_user_id, row_version FROM rcs.requirement WHERE id = @id")
            .With("id", requirementId)
            .SingleOrDefaultAsync(
                reader => new RequirementRow(
                    reader.Uuid("id"),
                    reader.Uuid("case_id"),
                    VocabularyCodes.FromCode<RequirementStatus>(reader.Text("status")),
                    reader.Bool("is_blocking"),
                    reader.UuidOrNull("source_response_id"),
                    reader.Uuid("created_by_user_id"),
                    reader.Int("row_version")),
                cancellationToken);

    /// <summary>Whether anything already builds on the requirement — a child request or recorded evidence (PERMISSIONS.md §24.1).</summary>
    public static async Task<bool> HasDependentsAsync(PostgresUnitOfWork unitOfWork, Guid requirementId, CancellationToken cancellationToken) =>
        await unitOfWork.Command("""
                SELECT EXISTS (SELECT 1 FROM rcs.request WHERE source_requirement_id = @id)
                    OR EXISTS (SELECT 1 FROM rcs.requirement_evidence WHERE requirement_id = @id AND status = 'ACTIVE')
                """)
            .With("id", requirementId)
            .ScalarAsync<bool>(cancellationToken);

    /// <summary>
    /// Applies <paramref name="setClause"/> under an optimistic-concurrency check. Returns the new row version, or null
    /// when someone else changed the row since the form was rendered (ARCHITECTURE.md §12.4).
    /// </summary>
    public static Task<int?> UpdateAsync(
        CommandScope scope,
        RequirementRow requirement,
        int rowVersion,
        string setClause,
        CancellationToken cancellationToken,
        Action<NpgsqlCommand>? parameters = null)
    {
        var command = scope.UnitOfWork.Command($"""
                UPDATE rcs.requirement
                SET {setClause}, updated_at = @now, updated_by_user_id = @actor, row_version = row_version + 1
                WHERE id = @id AND row_version = @row_version
                RETURNING row_version
                """)
            .With("now", scope.Now)
            .With("actor", scope.Actor.UserId)
            .With("id", requirement.Id)
            .With("row_version", rowVersion);
        parameters?.Invoke(command);
        return command.ScalarAsync<int?>(cancellationToken);
    }

    /// <summary>Q2 as a system consequence: a child request reaching SENT starts an OPEN requirement.</summary>
    public static async Task StartIfOpenAsync(CommandScope scope, Guid requirementId, Guid caseId, ActorKind actorKind, CancellationToken cancellationToken)
    {
        var requirement = await LoadAsync(scope.UnitOfWork, requirementId, cancellationToken);
        if (requirement is null || RequirementRules.CanStart(requirement.Status) is { IsAllowed: false })
        {
            return;
        }

        var version = await UpdateAsync(scope, requirement, requirement.RowVersion, "status = 'IN_PROGRESS', started_at = COALESCE(started_at, @now)", cancellationToken);
        if (version is null)
        {
            return;
        }

        await scope.AuditAsync(new AuditEntry(AuditActionCodes.StateChange, AuditEntityTypes.Requirement, requirementId, version, caseId,
            Before: new { status = requirement.Status.ToCode() }, After: new { status = "IN_PROGRESS" }, ActorKind: actorKind), cancellationToken);
    }

    /// <summary>
    /// After a requirement reaches a terminal state, the request whose response raised it is re-evaluated (WORKFLOW.md
    /// §5.6). It is never closed automatically — "ready to close" is surfaced, not acted on.
    /// </summary>
    public static async Task ApplyParentRequestConsequenceAsync(CommandScope scope, RequirementRow requirement, Guid caseId, CancellationToken cancellationToken)
    {
        if (requirement.SourceResponseId is not { } responseId)
        {
            return;
        }

        var requestId = await scope.UnitOfWork.Command("SELECT request_id FROM rcs.response WHERE id = @id")
            .With("id", responseId)
            .ScalarAsync<Guid?>(cancellationToken);
        if (requestId is { } parent)
        {
            await CaseSql.ApplyRequestConsequenceAsync(scope, parent, caseId, cancellationToken);
        }
    }
}
