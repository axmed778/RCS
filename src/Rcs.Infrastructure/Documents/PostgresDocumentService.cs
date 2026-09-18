using System.Data.Common;
using Microsoft.Extensions.Logging;
using Rcs.Application.Common;
using Rcs.Application.Concurrency;
using Rcs.Application.Documents;
using Rcs.Application.Idempotency;
using Rcs.Domain.Authorization;
using Rcs.Domain.Documents;
using Rcs.Domain.Vocabulary;
using Rcs.Domain.Workflow;
using Rcs.Infrastructure.Audit;
using Rcs.Infrastructure.Cases;
using Rcs.Infrastructure.Commands;
using Rcs.Infrastructure.Persistence;

namespace Rcs.Infrastructure.Documents;

/// <summary>
/// The document commands (DOCUMENT_MODEL.md §4–§8). Every upload follows §7.1: authorize first, then stream the bytes
/// into the object store and publish them, and only then open the one transaction that writes document, version,
/// placement, evidence, audit and operation receipt together. A failure after publishing leaves an unreferenced object
/// — retained, reported, harmless — and never a row pointing at missing bytes (§7.2).
/// </summary>
internal sealed class PostgresDocumentService(
    CommandRunner runner,
    LocalContentStore store,
    IOperationReceiptStore receipts,
    ICaseSerializationLock caseLock,
    Rcs.Application.Persistence.IUnitOfWorkFactory unitOfWorkFactory,
    ILogger<PostgresDocumentService> logger) : IDocumentService
{
    private const string UploadKind = "document.upload";
    private const string UploadVersionKind = "document.upload_version";

    // ----------------------------------------------------------------- uploads

    public async Task<CommandResult<UploadOutcome>> UploadDocumentAsync(ActorContext actor, UploadDocumentCommand command, UploadedContent content, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(content);

        // 1. Authorize, validate and answer a replay BEFORE reading a byte: a refused or repeated upload stores nothing.
        Guid? replayed = null;
        var check = await runner.RunAsync(actor, UploadKind + ".check", operationId: null, command.CaseId, async (scope, ct) =>
        {
            if (await ReplayAsync(scope, command.OperationId, UploadKind, ct) is { } receipt)
            {
                replayed = receipt.Value;
                return receipt.Result;
            }

            return await ValidateNewPlacementAsync(scope, command, ct) is { } refused ? refused : CommandResult<Guid>.Success(Guid.Empty);
        }, cancellationToken);
        if (!check.Succeeded)
        {
            return check.Cast<UploadOutcome>();
        }

        if (replayed is { } previousLink)
        {
            return await OutcomeOfLinkAsync(previousLink, cancellationToken);
        }

        // 2. Bytes first: stream, hash, flush, publish (§7.1 steps 1–7).
        var published = await StoreAsync(content, cancellationToken);
        if (published.Failure is { } storeFailure)
        {
            return storeFailure;
        }

        var (bytes, detected) = (published.Content!, published.Detected!);

        // 3. One transaction for every row (§7.1 steps 8–12).
        Guid? versionId = null;
        Guid? createdDocumentId = null;
        var result = await RunMetadataAsync(() => runner.RunAsync(actor, UploadKind, command.OperationId, command.CaseId, async (scope, ct) =>
        {
            if (await ValidateNewPlacementAsync(scope, command, ct) is { } refused)
            {
                return refused;
            }

            var kindId = (await DocumentSql.LookupIdAsync(scope.UnitOfWork, "document_kind", command.DocumentKindCode, ct))!.Value;
            var documentId = scope.NewId();
            var title = command.Title.Trim();
            await scope.UnitOfWork.Command("""
                    INSERT INTO rcs.document (id, document_kind_id, title, description, document_reference, status, created_at, created_by_user_id)
                    VALUES (@id, @kind, @title, @description, @reference, 'ACTIVE', @now, @actor)
                    """)
                .With("id", documentId)
                .With("kind", kindId)
                .With("title", title)
                .With("description", Blank(command.Description))
                .With("reference", Blank(command.DocumentReference))
                .With("now", scope.Now)
                .With("actor", scope.Actor.UserId)
                .ExecuteAsync(ct);

            var target = (await DocumentSql.ResolveTargetAsync(scope.UnitOfWork, command.Target, ct))!;
            await scope.AuditAsync(new AuditEntry(AuditActionCodes.Create, AuditEntityTypes.Document, documentId, 1, target.CaseId,
                After: new { title, document_kind = command.DocumentKindCode, document_reference = Blank(command.DocumentReference), status = "ACTIVE" }), ct);

            versionId = await InsertVersionAsync(scope, store.VolumeCode, documentId, 1, supersedes: null, bytes, detected, content, command.DocumentDate, target.CaseId, ct);
            createdDocumentId = documentId;

            var pin = command.Float && DocumentRules.MayFloat(command.Target.Kind, command.RoleCode, isHomeCase: true) ? (Guid?)null : versionId;
            var linkId = await InsertLinkAsync(scope, documentId, pin, command.Target, command.RoleCode, isOrigin: true, target.CaseId, disclosureReason: null, ct);

            if (command.RoleCode == DocumentLinkRoleCodes.RequirementEvidence)
            {
                await InsertEvidenceAsync(scope, command.Target.Id, documentId, versionId.Value, Blank(command.Note), target.CaseId, ct);
            }

            return CommandResult<Guid>.Success(linkId);
        }, cancellationToken), bytes);

        if (!result.Succeeded)
        {
            return result.Cast<UploadOutcome>();
        }

        // Without a captured id the result was answered from a receipt a concurrent attempt wrote.
        return versionId is { } created && createdDocumentId is { } document
            ? CommandResult<UploadOutcome>.Success(new UploadOutcome(document, created, result.Value, false))
            : await OutcomeOfLinkAsync(result.Value, cancellationToken);
    }

    public async Task<CommandResult<UploadOutcome>> UploadVersionAsync(ActorContext actor, UploadVersionCommand command, UploadedContent content, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(content);

        Guid? replayed = null;
        var check = await runner.RunAsync(actor, UploadVersionKind + ".check", operationId: null, command.CaseId, async (scope, ct) =>
        {
            if (await ReplayAsync(scope, command.OperationId, UploadVersionKind, ct) is { } receipt)
            {
                replayed = receipt.Value;
                return receipt.Result;
            }

            var loaded = await LoadForNewVersionAsync(scope, command, ct);
            return loaded.Failure ?? CommandResult<Guid>.Success(Guid.Empty);
        }, cancellationToken);
        if (!check.Succeeded)
        {
            return check.Cast<UploadOutcome>();
        }

        if (replayed is { } previousVersion)
        {
            return await OutcomeOfVersionAsync(previousVersion, cancellationToken);
        }

        var published = await StoreAsync(content, cancellationToken);
        if (published.Failure is { } storeFailure)
        {
            return storeFailure;
        }

        var (bytes, detected) = (published.Content!, published.Detected!);
        Guid? documentId = null;
        Guid? placementId = null;
        var converged = false;

        var result = await RunMetadataAsync(() => runner.RunAsync(actor, UploadVersionKind, command.OperationId, command.CaseId, async (scope, ct) =>
        {
            var loaded = await LoadForNewVersionAsync(scope, command, ct);
            if (loaded.Failure is { } refused)
            {
                return refused;
            }

            var document = loaded.Document!;
            documentId = document.Id;
            var versions = await DocumentSql.VersionsOfDocumentAsync(scope.UnitOfWork, document.Id, ct);

            // §7.4: identical bytes converge on the existing version. Convergence never changes a version's status;
            // an older version is made current by reinstatement, not by re-uploading it.
            if (versions.FirstOrDefault(version => version.ContentHash == bytes.ContentHash) is { } existing)
            {
                converged = true;
                if (command.Placement is { } again)
                {
                    placementId = await PlaceIfAbsentAsync(scope, document.Id, existing.Id, again, command.PlacementRoleCode!, command.CaseId, ct);
                }

                return CommandResult<Guid>.Success(existing.Id);
            }

            var current = versions.FirstOrDefault(version => version.Status == DocumentVersionStatus.Active);
            var floating = (await DocumentSql.ActiveLinksOfDocumentAsync(scope.UnitOfWork, document.Id, ct)).Any(active => active.VersionId is null);
            if (!floating && command.Placement is null)
            {
                // A version exposed by no placement could be read by nobody (§10.1).
                return Invalid("document.version_needs_placement", nameof(command.Placement));
            }

            if (current is not null)
            {
                var superseded = await scope.UnitOfWork.Command("""
                        UPDATE rcs.document_version
                        SET status = 'SUPERSEDED', updated_at = @now, updated_by_user_id = @actor, row_version = row_version + 1
                        WHERE id = @id AND status = 'ACTIVE'
                        RETURNING row_version
                        """)
                    .With("now", scope.Now).With("actor", scope.Actor.UserId).With("id", current.Id)
                    .ScalarAsync<int?>(ct) ?? throw new InvalidOperationException("The current version changed under the document lock.");

                await scope.AuditAsync(new AuditEntry(AuditActionCodes.StateChange, AuditEntityTypes.DocumentVersion, current.Id, superseded, command.CaseId,
                    Before: new { status = "ACTIVE" }, After: new { status = "SUPERSEDED", version_no = current.VersionNo }, ActorKind: ActorKind.System), ct);
            }

            var versionNo = versions.Count == 0 ? 1 : versions.Max(version => version.VersionNo) + 1;
            var versionId = await InsertVersionAsync(scope, store.VolumeCode, document.Id, versionNo, current?.Id, bytes, detected, content, command.DocumentDate, command.CaseId, ct);

            if (command.Placement is { } placement)
            {
                placementId = await InsertLinkAsync(scope, document.Id, versionId, placement, command.PlacementRoleCode!, isOrigin: false, command.CaseId, null, ct);
            }

            return CommandResult<Guid>.Success(versionId);
        }, cancellationToken), bytes);

        if (!result.Succeeded)
        {
            return result.Cast<UploadOutcome>();
        }

        return documentId is null
            ? await OutcomeOfVersionAsync(result.Value, cancellationToken)
            : CommandResult<UploadOutcome>.Success(new UploadOutcome(documentId.Value, result.Value, placementId, converged));
    }

    // -------------------------------------------------------------- placements

    public Task<CommandResult<Guid>> PlaceVersionAsync(ActorContext actor, PlaceVersionCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        return runner.RunAsync(actor, "document.place", command.OperationId, command.CaseId, async (scope, ct) =>
        {
            if (command.RoleCode == DocumentLinkRoleCodes.RequirementEvidence)
            {
                return Invalid("document.use_evidence_command", nameof(command.RoleCode));
            }

            var source = await LoadExposedSourceAsync(scope, command.SourceLinkId, command.VersionId, ct);
            if (source.Failure is { } failure)
            {
                return failure;
            }

            var version = source.Version!;
            if (await ValidatePlacementTargetAsync(scope, command.CaseId, command.Target, command.RoleCode, version.DocumentId, command.Reason, ct) is { } refused)
            {
                return refused;
            }

            if (await AlreadyPlacedAsync(scope, version.DocumentId, version.Id, command.Target, ct))
            {
                return Rule("document.already_placed");
            }

            var home = await DocumentSql.HomeCaseAsync(scope.UnitOfWork, version.DocumentId, ct);
            var disclosure = home != command.CaseId ? Blank(command.Reason) : null;
            return CommandResult<Guid>.Success(await InsertLinkAsync(scope, version.DocumentId, version.Id, command.Target, command.RoleCode, false, command.CaseId, disclosure, ct));
        }, cancellationToken);
    }

    public Task<CommandResult<Guid>> RecordDocumentEvidenceAsync(ActorContext actor, RecordDocumentEvidenceCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        return runner.RunAsync(actor, "document.evidence", command.OperationId, command.CaseId, async (scope, ct) =>
        {
            var source = await LoadExposedSourceAsync(scope, command.SourceLinkId, command.VersionId, ct);
            if (source.Failure is { } failure)
            {
                return failure;
            }

            var version = source.Version!;
            var target = new DocumentTarget(DocumentTargetKind.Requirement, command.RequirementId);
            if (await ValidatePlacementTargetAsync(scope, command.CaseId, target, DocumentLinkRoleCodes.RequirementEvidence, version.DocumentId, command.DisclosureReason, ct) is { } refused)
            {
                return refused;
            }

            var duplicate = await scope.UnitOfWork.Command("""
                    SELECT EXISTS (SELECT 1 FROM rcs.requirement_evidence
                                   WHERE requirement_id = @requirement AND document_version_id = @version AND status = 'ACTIVE')
                    """)
                .With("requirement", command.RequirementId).With("version", version.Id).ScalarAsync<bool>(ct);
            if (duplicate)
            {
                return Rule("document.already_evidence");
            }

            var home = await DocumentSql.HomeCaseAsync(scope.UnitOfWork, version.DocumentId, ct);
            var disclosure = home != command.CaseId ? Blank(command.DisclosureReason) : null;
            await InsertLinkAsync(scope, version.DocumentId, version.Id, target, DocumentLinkRoleCodes.RequirementEvidence, false, command.CaseId, disclosure, ct);
            return CommandResult<Guid>.Success(await InsertEvidenceAsync(scope, command.RequirementId, version.DocumentId, version.Id, Blank(command.Note), command.CaseId, ct));
        }, cancellationToken);
    }

    public Task<CommandResult<Guid>> RetractEvidenceAsync(ActorContext actor, RetractEvidenceCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        return runner.RunAsync(actor, "document.evidence_retract", operationId: null, command.CaseId, async (scope, ct) =>
        {
            var evidence = await scope.UnitOfWork.Command("""
                    SELECT e.id, e.requirement_id, e.evidence_type, e.document_id, e.document_version_id, e.status,
                           e.recorded_by_user_id, q.case_id, q.status AS requirement_status
                    FROM rcs.requirement_evidence AS e JOIN rcs.requirement AS q ON q.id = e.requirement_id
                    WHERE e.id = @id
                    """)
                .With("id", command.EvidenceId)
                .SingleOrDefaultAsync(reader => new
                {
                    RequirementId = reader.Uuid("requirement_id"),
                    Type = reader.Text("evidence_type"),
                    DocumentId = reader.UuidOrNull("document_id"),
                    VersionId = reader.UuidOrNull("document_version_id"),
                    Status = VocabularyCodes.FromCode<EvidenceStatus>(reader.Text("status")),
                    RecordedBy = reader.Uuid("recorded_by_user_id"),
                    CaseId = reader.Uuid("case_id"),
                    RequirementStatus = VocabularyCodes.FromCode<RequirementStatus>(reader.Text("requirement_status")),
                }, ct);
            if (evidence is null || evidence.CaseId != command.CaseId)
            {
                return NotFound("evidence");
            }

            var caseRow = (await CaseSql.LoadAsync(scope.UnitOfWork, command.CaseId, ct))!;
            var relationship = await RelationshipAsync(scope, caseRow, evidence.RecordedBy == scope.Actor.UserId, ct);
            if (scope.Refuse(BusinessAction.RetractRequirementEvidence, relationship, AuditEntityTypes.RequirementEvidence, command.EvidenceId, caseRow.Id) is { } denied)
            {
                return denied;
            }

            if (evidence.Status != EvidenceStatus.Active)
            {
                return Rule("document.evidence_not_active");
            }

            // A resolved requirement is returned to an open state first (Q7); its evidence is then retracted row by row.
            if (evidence.RequirementStatus.IsTerminal())
            {
                return Rule("document.evidence_locked_by_resolution");
            }

            var note = Blank(command.Note);
            if (note is null)
            {
                return Invalid("validation.required", nameof(command.Note));
            }

            var version = await scope.UnitOfWork.Command("""
                    UPDATE rcs.requirement_evidence
                    SET status = 'RETRACTED', retracted_at = @now, retracted_by_user_id = @actor, retraction_note = @note,
                        updated_at = @now, updated_by_user_id = @actor, row_version = row_version + 1
                    WHERE id = @id AND row_version = @row_version AND status = 'ACTIVE'
                    RETURNING row_version
                    """)
                .With("now", scope.Now).With("actor", scope.Actor.UserId).With("note", note)
                .With("id", command.EvidenceId).With("row_version", command.RowVersion)
                .ScalarAsync<int?>(ct);
            if (version is null)
            {
                return Conflict();
            }

            await scope.AuditAsync(new AuditEntry(AuditActionCodes.Unlink, AuditEntityTypes.RequirementEvidence, command.EvidenceId, version, caseRow.Id,
                Before: new { status = "ACTIVE" },
                After: new { status = "RETRACTED", requirement_id = evidence.RequirementId, evidence_type = evidence.Type, document_version_id = evidence.VersionId },
                ReasonNote: note), ct);

            // §4.6: retracting document evidence retires its placement in the same act.
            if (evidence.VersionId is { } versionId)
            {
                var links = await scope.UnitOfWork.Command("""
                        SELECT id, row_version FROM rcs.document_link
                        WHERE requirement_id = @requirement AND document_version_id = @version AND status = 'ACTIVE'
                          AND document_link_role_id = (SELECT id FROM rcs.document_link_role WHERE code = 'REQUIREMENT_EVIDENCE')
                        """)
                    .With("requirement", evidence.RequirementId).With("version", versionId)
                    .ListAsync(reader => (Id: reader.Uuid("id"), RowVersion: reader.Int("row_version")), ct);
                foreach (var link in links)
                {
                    await RemoveLinkRowAsync(scope, link.Id, link.RowVersion, note, caseRow.Id, ActorKind.System, ct);
                }
            }

            return CommandResult<Guid>.Success(command.EvidenceId);
        }, cancellationToken);
    }

    // ------------------------------------------------------------- corrections

    public Task<CommandResult<Guid>> RemoveLinkAsync(ActorContext actor, RemoveLinkCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        return runner.RunAsync(actor, "document.unlink", operationId: null, command.CaseId, async (scope, ct) =>
        {
            var link = await DocumentSql.LoadLinkAsync(scope.UnitOfWork, command.LinkId, ct);
            if (link is null || link.ContextCaseId != command.CaseId)
            {
                return NotFound("document");
            }

            var caseRow = (await CaseSql.LoadAsync(scope.UnitOfWork, command.CaseId, ct))!;
            var own = link.LinkedByUserId == scope.Actor.UserId && !await DocumentSql.LinkHasDependentsAsync(scope.UnitOfWork, link, ct);
            if (scope.Refuse(BusinessAction.CorrectDocument, await RelationshipAsync(scope, caseRow, own, ct), AuditEntityTypes.DocumentLink, link.Id, caseRow.Id) is { } denied)
            {
                return denied;
            }

            var others = (await DocumentSql.ActiveLinksOfDocumentAsync(scope.UnitOfWork, link.DocumentId, ct)).Count(active => active.Id != link.Id);
            var decision = link.Target.Kind == DocumentTargetKind.FinalResult
                ? (await DocumentSql.ResolveTargetAsync(scope.UnitOfWork, link.Target, ct))?.FinalResultStatus
                : null;
            if (DocumentRules.CanRemoveLink(link.Status, link.RoleCode, link.IsOrigin, others, decision, command.Reason) is { IsAllowed: false } rule)
            {
                return Rule(rule.ViolationCode!);
            }

            return await RemoveLinkRowAsync(scope, link.Id, command.RowVersion, command.Reason.Trim(), caseRow.Id, ActorKind.User, ct)
                ? CommandResult<Guid>.Success(link.Id)
                : Conflict();
        }, cancellationToken);
    }

    public Task<CommandResult<Guid>> MoveLinkAsync(ActorContext actor, MoveLinkCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        // Possibly two cases: both are locked, in a fixed order, before anything is read (ADR-019).
        return runner.RunAsync(actor, "document.move", operationId: null, caseId: null, async (scope, ct) =>
        {
            await caseLock.AcquireAsync(scope.UnitOfWork, new[] { command.CaseId, command.TargetCaseId }.Distinct().ToArray(), ct);

            var link = await DocumentSql.LoadLinkAsync(scope.UnitOfWork, command.LinkId, ct);
            if (link is null || link.ContextCaseId != command.CaseId)
            {
                return NotFound("document");
            }

            var caseRow = (await CaseSql.LoadAsync(scope.UnitOfWork, command.CaseId, ct))!;
            var own = link.LinkedByUserId == scope.Actor.UserId && !await DocumentSql.LinkHasDependentsAsync(scope.UnitOfWork, link, ct);
            if (scope.Refuse(BusinessAction.CorrectDocument, await RelationshipAsync(scope, caseRow, own, ct), AuditEntityTypes.DocumentLink, link.Id, caseRow.Id) is { } denied)
            {
                return denied;
            }

            if (link.Status != DocumentLinkStatus.Active)
            {
                return Rule("document.link_not_active");
            }

            if (link.RoleCode == DocumentLinkRoleCodes.RequirementEvidence || command.NewRoleCode == DocumentLinkRoleCodes.RequirementEvidence)
            {
                return Rule("document.evidence_link_retract_instead");
            }

            if (link.Target.Kind == DocumentTargetKind.FinalResult
                && (await DocumentSql.ResolveTargetAsync(scope.UnitOfWork, link.Target, ct))?.FinalResultStatus is { } decided && decided != FinalResultStatus.Draft)
            {
                return Rule("document.version_pinned_by_decision");
            }

            if (link.Target == command.NewTarget && link.RoleCode == command.NewRoleCode)
            {
                return Rule("document.move_same_place");
            }

            var reason = Blank(command.Reason);
            if (reason is null)
            {
                return Invalid("document.removal_reason_required", nameof(command.Reason));
            }

            var home = await DocumentSql.HomeCaseAsync(scope.UnitOfWork, link.DocumentId, ct);
            var movesHome = link.IsOrigin && command.TargetCaseId != command.CaseId;
            var newHome = movesHome ? command.TargetCaseId : home;
            if (await ValidatePlacementTargetAsync(scope, command.TargetCaseId, command.NewTarget, command.NewRoleCode, link.DocumentId,
                    reason, ct, homeOverride: newHome, crossCaseEvenIfHome: command.TargetCaseId != command.CaseId) is { } refused)
            {
                return refused;
            }

            // Retire first, so a role swap on the same letter never sees two primary letters (L3).
            if (!await RemoveLinkRowAsync(scope, link.Id, command.RowVersion, reason, caseRow.Id, ActorKind.User, ct))
            {
                return Conflict();
            }

            var pin = link.VersionId;
            if (pin is null && !DocumentRules.MayFloat(command.NewTarget.Kind, command.NewRoleCode, newHome == command.TargetCaseId))
            {
                pin = (await DocumentSql.VersionsOfDocumentAsync(scope.UnitOfWork, link.DocumentId, ct))
                    .FirstOrDefault(version => version.Status == DocumentVersionStatus.Active)?.Id;
                if (pin is null)
                {
                    return Rule("document.no_current_version");
                }
            }

            var disclosure = command.TargetCaseId != command.CaseId ? reason : null;
            var newLink = await InsertLinkAsync(scope, link.DocumentId, pin, command.NewTarget, command.NewRoleCode, link.IsOrigin, command.TargetCaseId, disclosure, ct);

            // §4.4 "if the home case moves": floating placements left in the old home case are frozen to the version they
            // present, so no floating placement ever sits outside the home case.
            if (movesHome)
            {
                await FreezeFloatingAsync(scope, link.DocumentId, command.CaseId, ct);
            }

            return CommandResult<Guid>.Success(newLink);
        }, cancellationToken);
    }

    public Task<CommandResult<Guid>> WithdrawVersionAsync(ActorContext actor, WithdrawVersionCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        return runner.RunAsync(actor, "document.withdraw_version", operationId: null, command.CaseId, async (scope, ct) =>
        {
            var loaded = await LoadHomeDocumentAsync(scope, command.CaseId, command.ViaLinkId, command.VersionId, ct);
            if (loaded.Failure is { } failure)
            {
                return failure;
            }

            var (caseRow, version) = (loaded.Case!, loaded.Version!);
            var own = version.UploadedByUserId == scope.Actor.UserId
                && !await DocumentSql.HasDependentsAsync(scope.UnitOfWork, version.DocumentId, version.Id, ct);
            if (scope.Refuse(BusinessAction.CorrectDocument, await RelationshipAsync(scope, caseRow, own, ct), AuditEntityTypes.DocumentVersion, version.Id, caseRow.Id) is { } denied)
            {
                return denied;
            }

            var pinnedByDecision = await DocumentSql.PinnedByIssuedDecisionAsync(scope.UnitOfWork, version.Id, ct);
            if (DocumentRules.CanWithdrawVersion(version.Status, pinnedByDecision, command.ReasonCode) is { IsAllowed: false } rule)
            {
                return Rule(rule.ViolationCode!);
            }

            var reasonId = await DocumentSql.LookupIdAsync(scope.UnitOfWork, "withdrawal_reason", command.ReasonCode, ct);
            if (reasonId is null)
            {
                return Invalid("document.withdrawal_reason_required", nameof(command.ReasonCode));
            }

            var reinstatementReason = Blank(command.ReinstatementReason) ?? Blank(command.Note);
            if (command.ReinstatePrevious && version.Status == DocumentVersionStatus.Active && reinstatementReason is null)
            {
                return Invalid("document.reinstatement_reason_required", nameof(command.ReinstatementReason));
            }

            var withdrawn = await scope.UnitOfWork.Command("""
                    UPDATE rcs.document_version
                    SET status = 'WITHDRAWN', withdrawn_at = @now, withdrawn_by_user_id = @actor, withdrawal_reason_id = @reason,
                        withdrawal_note = @note, updated_at = @now, updated_by_user_id = @actor, row_version = row_version + 1
                    WHERE id = @id AND row_version = @row_version AND status <> 'WITHDRAWN'
                    RETURNING row_version
                    """)
                .With("now", scope.Now).With("actor", scope.Actor.UserId).With("reason", reasonId).With("note", Blank(command.Note))
                .With("id", version.Id).With("row_version", command.RowVersion)
                .ScalarAsync<int?>(ct);
            if (withdrawn is null)
            {
                return Conflict();
            }

            await scope.AuditAsync(new AuditEntry(AuditActionCodes.Withdraw, AuditEntityTypes.DocumentVersion, version.Id, withdrawn, caseRow.Id,
                Before: new { status = version.Status.ToCode() },
                After: new { status = "WITHDRAWN", version_no = version.VersionNo, withdrawal_reason = command.ReasonCode },
                ReasonNote: Blank(command.Note), DocumentHash: version.ContentHash), ct);

            // ADR-027: withdrawing the current version never reinstates anything by itself; when asked, in the same act.
            if (command.ReinstatePrevious && version.Status == DocumentVersionStatus.Active)
            {
                var versions = await DocumentSql.VersionsOfDocumentAsync(scope.UnitOfWork, version.DocumentId, ct);
                if (DocumentRules.ReinstatementCandidate(Facts(versions)) is { } candidate)
                {
                    await ReinstateRowAsync(scope, versions.Single(row => row.Id == candidate.Id), reinstatementReason!, version.Id, caseRow.Id, ct);
                }
            }

            return CommandResult<Guid>.Success(version.Id);
        }, cancellationToken);
    }

    public Task<CommandResult<Guid>> ReinstateVersionAsync(ActorContext actor, ReinstateVersionCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        return runner.RunAsync(actor, "document.reinstate_version", operationId: null, command.CaseId, async (scope, ct) =>
        {
            var loaded = await LoadHomeDocumentAsync(scope, command.CaseId, command.ViaLinkId, command.VersionId, ct);
            if (loaded.Failure is { } failure)
            {
                return failure;
            }

            var (caseRow, version) = (loaded.Case!, loaded.Version!);
            var versions = await DocumentSql.VersionsOfDocumentAsync(scope.UnitOfWork, version.DocumentId, ct);

            // PROVISIONAL: reinstatement follows the authority of the withdrawal it corrects (§8.2 case 3): the person
            // who uploaded the withdrawn version while nothing depends on the document, otherwise a Chief.
            var lastWithdrawn = versions.Where(row => row.Status == DocumentVersionStatus.Withdrawn).MaxBy(row => row.VersionNo);
            var own = lastWithdrawn?.UploadedByUserId == scope.Actor.UserId
                && !await DocumentSql.HasDependentsAsync(scope.UnitOfWork, version.DocumentId, null, ct);
            if (scope.Refuse(BusinessAction.CorrectDocument, await RelationshipAsync(scope, caseRow, own, ct), AuditEntityTypes.DocumentVersion, version.Id, caseRow.Id) is { } denied)
            {
                return denied;
            }

            if (DocumentRules.CanReinstate(Facts(versions), version.Id, command.Reason) is { IsAllowed: false } rule)
            {
                return Rule(rule.ViolationCode!);
            }

            if (version.RowVersion != command.RowVersion)
            {
                return Conflict();
            }

            await ReinstateRowAsync(scope, version, command.Reason.Trim(), lastWithdrawn?.Id, caseRow.Id, ct);
            return CommandResult<Guid>.Success(version.Id);
        }, cancellationToken);
    }

    public Task<CommandResult<Guid>> WithdrawDocumentAsync(ActorContext actor, WithdrawDocumentCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        return runner.RunAsync(actor, "document.withdraw", operationId: null, command.CaseId, async (scope, ct) =>
        {
            var loaded = await LoadHomeDocumentAsync(scope, command.CaseId, command.ViaLinkId, versionId: null, ct);
            if (loaded.Failure is { } failure)
            {
                return failure;
            }

            var (caseRow, document) = (loaded.Case!, loaded.Document!);
            var links = await DocumentSql.ActiveLinksOfDocumentAsync(scope.UnitOfWork, document.Id, ct);
            var own = document.CreatedByUserId == scope.Actor.UserId
                && links.All(link => link.ContextCaseId == caseRow.Id)
                && !await DocumentSql.HasDependentsAsync(scope.UnitOfWork, document.Id, null, ct);
            if (scope.Refuse(BusinessAction.CorrectDocument, await RelationshipAsync(scope, caseRow, own, ct), AuditEntityTypes.Document, document.Id, caseRow.Id) is { } denied)
            {
                return denied;
            }

            if (document.Status != DocumentStatus.Active)
            {
                return Rule("document.not_active");
            }

            var versions = await DocumentSql.VersionsOfDocumentAsync(scope.UnitOfWork, document.Id, ct);
            foreach (var version in versions)
            {
                if (await DocumentSql.PinnedByIssuedDecisionAsync(scope.UnitOfWork, version.Id, ct))
                {
                    return Rule("document.version_pinned_by_decision");
                }
            }

            // Evidence is never retracted as a side effect (never cascade): retract it first, deliberately.
            if (links.Any(link => link.RoleCode == DocumentLinkRoleCodes.RequirementEvidence))
            {
                return Rule("document.cited_as_evidence");
            }

            var reasonId = await DocumentSql.LookupIdAsync(scope.UnitOfWork, "withdrawal_reason", command.ReasonCode, ct);
            if (reasonId is null)
            {
                return Invalid("document.withdrawal_reason_required", nameof(command.ReasonCode));
            }

            var note = Blank(command.Note);
            var documentVersion = await scope.UnitOfWork.Command("""
                    UPDATE rcs.document
                    SET status = 'WITHDRAWN', withdrawn_at = @now, withdrawn_by_user_id = @actor, withdrawal_reason_id = @reason,
                        withdrawal_note = @note, updated_at = @now, updated_by_user_id = @actor, row_version = row_version + 1
                    WHERE id = @id AND row_version = @row_version AND status = 'ACTIVE'
                    RETURNING row_version
                    """)
                .With("now", scope.Now).With("actor", scope.Actor.UserId).With("reason", reasonId).With("note", note)
                .With("id", document.Id).With("row_version", command.DocumentRowVersion)
                .ScalarAsync<int?>(ct);
            if (documentVersion is null)
            {
                return Conflict();
            }

            await scope.AuditAsync(new AuditEntry(AuditActionCodes.Withdraw, AuditEntityTypes.Document, document.Id, documentVersion, caseRow.Id,
                Before: new { status = "ACTIVE" }, After: new { status = "WITHDRAWN", title = document.Title, withdrawal_reason = command.ReasonCode }, ReasonNote: note), ct);

            foreach (var version in versions.Where(row => row.Status != DocumentVersionStatus.Withdrawn))
            {
                var rowVersion = await scope.UnitOfWork.Command("""
                        UPDATE rcs.document_version
                        SET status = 'WITHDRAWN', withdrawn_at = @now, withdrawn_by_user_id = @actor, withdrawal_reason_id = @reason,
                            withdrawal_note = @note, updated_at = @now, updated_by_user_id = @actor, row_version = row_version + 1
                        WHERE id = @id
                        RETURNING row_version
                        """)
                    .With("now", scope.Now).With("actor", scope.Actor.UserId).With("reason", reasonId).With("note", note).With("id", version.Id)
                    .ScalarAsync<int?>(ct);
                await scope.AuditAsync(new AuditEntry(AuditActionCodes.Withdraw, AuditEntityTypes.DocumentVersion, version.Id, rowVersion, caseRow.Id,
                    Before: new { status = version.Status.ToCode() }, After: new { status = "WITHDRAWN", version_no = version.VersionNo },
                    ReasonNote: note, ActorKind: ActorKind.System, DocumentHash: version.ContentHash), ct);
            }

            foreach (var link in links)
            {
                await RemoveLinkRowAsync(scope, link.Id, link.RowVersion, note ?? command.ReasonCode, link.ContextCaseId, ActorKind.System, ct);
            }

            return CommandResult<Guid>.Success(document.Id);
        }, cancellationToken);
    }

    public Task<CommandResult<Guid>> EditMetadataAsync(ActorContext actor, EditDocumentMetadataCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        return runner.RunAsync(actor, "document.edit", operationId: null, command.CaseId, async (scope, ct) =>
        {
            var loaded = await LoadHomeDocumentAsync(scope, command.CaseId, command.ViaLinkId, versionId: null, ct);
            if (loaded.Failure is { } failure)
            {
                return failure;
            }

            var (caseRow, document) = (loaded.Case!, loaded.Document!);
            if (scope.Refuse(BusinessAction.EditDocumentMetadata, await RelationshipAsync(scope, caseRow, false, ct), AuditEntityTypes.Document, document.Id, caseRow.Id) is { } denied)
            {
                return denied;
            }

            var title = Blank(command.Title);
            if (title is null)
            {
                return Invalid("validation.required", nameof(command.Title));
            }

            var kindId = await DocumentSql.LookupIdAsync(scope.UnitOfWork, "document_kind", command.DocumentKindCode, ct);
            if (kindId is null)
            {
                return Invalid("validation.required", nameof(command.DocumentKindCode));
            }

            var version = await scope.UnitOfWork.Command("""
                    UPDATE rcs.document
                    SET title = @title, document_kind_id = @kind, description = @description, document_reference = @reference,
                        updated_at = @now, updated_by_user_id = @actor, row_version = row_version + 1
                    WHERE id = @id AND row_version = @row_version
                    RETURNING row_version
                    """)
                .With("title", title).With("kind", kindId).With("description", Blank(command.Description))
                .With("reference", Blank(command.DocumentReference)).With("now", scope.Now).With("actor", scope.Actor.UserId)
                .With("id", document.Id).With("row_version", command.DocumentRowVersion)
                .ScalarAsync<int?>(ct);
            if (version is null)
            {
                return Conflict();
            }

            await scope.AuditAsync(new AuditEntry(AuditActionCodes.Update, AuditEntityTypes.Document, document.Id, version, caseRow.Id,
                Before: new { title = document.Title, document_kind = document.KindCode, description = document.Description, document_reference = document.DocumentReference },
                After: new { title, document_kind = command.DocumentKindCode, description = Blank(command.Description), document_reference = Blank(command.DocumentReference) }), ct);

            return CommandResult<Guid>.Success(document.Id);
        }, cancellationToken);
    }

    // ------------------------------------------------------------------ plumbing

    private sealed record Replay(Guid Value, CommandResult<Guid> Result);

    /// <summary>An upload whose operation id is already recorded returns the recorded result and stores nothing (ADR-020).</summary>
    private async Task<Replay?> ReplayAsync(CommandScope scope, OperationId operationId, string kind, CancellationToken cancellationToken)
    {
        var receipt = await receipts.FindAsync(scope.UnitOfWork, operationId, cancellationToken);
        if (receipt is null)
        {
            return null;
        }

        return receipt.ActorUserId == scope.Actor.UserId && receipt.OperationKind == kind && Guid.TryParse(receipt.ResultReference, out var value)
            ? new Replay(value, CommandResult<Guid>.Success(value))
            : new Replay(Guid.Empty, CommandResult<Guid>.Failure(CommandErrorKind.Conflict, "operation.already_used"));
    }

    private sealed record Stored(StagedContent? Content, DetectedFileType? Detected, CommandResult<UploadOutcome>? Failure);

    private async Task<Stored> StoreAsync(UploadedContent content, CancellationToken cancellationToken)
    {
        StagedContent staged;
        try
        {
            staged = await store.StageAsync(content.Content, cancellationToken);
        }
        catch (UploadTooLargeException)
        {
            return new Stored(null, null, CommandResult<UploadOutcome>.Failure(CommandErrorKind.Validation, "document.too_large", "File"));
        }
        catch (ContentStoreUnavailableException exception)
        {
            logger.LogError(exception, "The document store is unavailable.");
            return new Stored(null, null, CommandResult<UploadOutcome>.Failure(CommandErrorKind.RuleViolation, "document.storage_unavailable"));
        }
        catch (IOException exception)
        {
            // An interrupted request body: the temporary file is already gone, nothing reached the store (§7.4).
            logger.LogWarning(exception, "An upload was interrupted before it completed.");
            return new Stored(null, null, CommandResult<UploadOutcome>.Failure(CommandErrorKind.Validation, "document.upload_incomplete", "File"));
        }

        if (staged.ByteSize == 0)
        {
            store.DiscardStaged(staged);
            return new Stored(null, null, CommandResult<UploadOutcome>.Failure(CommandErrorKind.Validation, "document.empty_file", "File"));
        }

        try
        {
            // The staged record now carries the published object; the temporary file is gone either way.
            store.Publish(staged);
        }
        catch (Exception exception) when (exception is ContentStoreUnavailableException or IOException or UnauthorizedAccessException)
        {
            logger.LogError(exception, "A staged upload could not be published into the object store.");
            store.DiscardStaged(staged);
            return new Stored(null, null, CommandResult<UploadOutcome>.Failure(CommandErrorKind.RuleViolation, "document.storage_unavailable"));
        }

        return new Stored(staged, FileTypePolicy.Detect(staged.Head, content.OriginalFileName), null);
    }

    /// <summary>
    /// Runs the metadata transaction. If it fails for any reason other than a refusal, the application operation has
    /// failed: the published object stays, unreferenced (§7.2), and is never deleted automatically (ADR-026).
    /// </summary>
    private async Task<CommandResult<Guid>> RunMetadataAsync(Func<Task<CommandResult<Guid>>> transaction, StagedContent bytes)
    {
        try
        {
            return await transaction();
        }
        catch (DbException exception)
        {
            logger.LogError(exception, "Recording the metadata of stored object {Hash} failed; the object is retained unreferenced.", bytes.ContentHash);
            return CommandResult<Guid>.Failure(CommandErrorKind.RuleViolation, "document.upload_failed");
        }
    }

    /// <summary>Everything a new document's first placement requires, checked before the bytes and again inside the transaction.</summary>
    private static async Task<CommandResult<Guid>?> ValidateNewPlacementAsync(CommandScope scope, UploadDocumentCommand command, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(command.Title))
        {
            return Invalid("validation.required", nameof(command.Title));
        }

        if (await DocumentSql.LookupIdAsync(scope.UnitOfWork, "document_kind", command.DocumentKindCode, cancellationToken) is null)
        {
            return Invalid("validation.required", nameof(command.DocumentKindCode));
        }

        // A new document's origin fixes its home case, so its first placement is by definition in the home case.
        return await ValidatePlacementTargetAsync(scope, command.CaseId, command.Target, command.RoleCode, documentId: null, reason: null, cancellationToken);
    }

    /// <summary>
    /// The target exists in <paramref name="caseId"/>, accepts the role, is in a state that accepts it, and the actor may
    /// place a file there. A placement outside the document's home case is a disclosure: Chief, with a reason (§4.8).
    /// </summary>
    private static async Task<CommandResult<Guid>?> ValidatePlacementTargetAsync(
        CommandScope scope,
        Guid caseId,
        DocumentTarget target,
        string roleCode,
        Guid? documentId,
        string? reason,
        CancellationToken cancellationToken,
        Guid? homeOverride = null,
        bool crossCaseEvenIfHome = false)
    {
        var resolved = await DocumentSql.ResolveTargetAsync(scope.UnitOfWork, target, cancellationToken);
        var caseRow = await CaseSql.LoadAsync(scope.UnitOfWork, caseId, cancellationToken);
        if (resolved is null || caseRow is null || resolved.CaseId != caseId)
        {
            return NotFound("target");
        }

        if (!DocumentRules.RoleFitsTarget(target.Kind, roleCode)
            || await DocumentSql.LookupIdAsync(scope.UnitOfWork, "document_link_role", roleCode, cancellationToken) is null)
        {
            return Invalid("document.role_not_allowed_here", "RoleCode");
        }

        var relationship = await RelationshipAsync(scope, caseRow, false, cancellationToken);
        if (scope.Refuse(BusinessAction.UploadDocument, relationship, DocumentSql.TargetEntityType(target.Kind), target.Id, caseRow.Id) is { } denied)
        {
            return denied;
        }

        if (documentId is { } existing)
        {
            var home = homeOverride ?? await DocumentSql.HomeCaseAsync(scope.UnitOfWork, existing, cancellationToken);
            if (home != caseId || crossCaseEvenIfHome)
            {
                if (scope.Refuse(BusinessAction.LinkDocumentIntoAnotherCase, relationship, AuditEntityTypes.Document, existing, caseRow.Id) is { } notChief)
                {
                    return notChief;
                }

                if (string.IsNullOrWhiteSpace(reason))
                {
                    return Invalid("document.disclosure_reason_required", "Reason");
                }
            }
        }

        switch (target.Kind)
        {
            case DocumentTargetKind.Requirement when roleCode == DocumentLinkRoleCodes.RequirementEvidence:
                if (scope.Refuse(BusinessAction.RecordRequirementEvidence, relationship, AuditEntityTypes.Requirement, target.Id, caseRow.Id) is { } notAssigned)
                {
                    return notAssigned;
                }

                if (resolved.RequirementStatus is { } status && status.IsTerminal())
                {
                    return Rule("document.requirement_resolved");
                }

                break;

            case DocumentTargetKind.FinalResult:
                if (scope.Refuse(BusinessAction.AttachFinalResultDocument, relationship, AuditEntityTypes.FinalResult, target.Id, caseRow.Id) is { } notChiefResult)
                {
                    return notChiefResult;
                }

                // A draft takes its documents; an issued decision may still receive the signed copy that "must follow"
                // a D4 override. A superseded, revoked or void decision is history.
                if (resolved.FinalResultStatus is not (FinalResultStatus.Draft or FinalResultStatus.Issued))
                {
                    return Rule("document.final_result_closed");
                }

                break;

            case DocumentTargetKind.Correspondence when roleCode == DocumentLinkRoleCodes.PrimaryLetter:
                var hasPrimary = await scope.UnitOfWork.Command("""
                        SELECT EXISTS (SELECT 1 FROM rcs.document_link
                                       WHERE correspondence_id = @letter AND status = 'ACTIVE'
                                         AND document_link_role_id = (SELECT id FROM rcs.document_link_role WHERE code = 'PRIMARY_LETTER'))
                        """)
                    .With("letter", target.Id).ScalarAsync<bool>(cancellationToken);
                if (hasPrimary)
                {
                    return Rule("document.primary_letter_exists");
                }

                break;
        }

        return null;
    }

    /// <summary>The placement a new version is uploaded through: an ACTIVE placement in the home case the actor can see.</summary>
    private static async Task<(LinkRow? Link, DocumentRow? Document, CommandResult<Guid>? Failure)> LoadForNewVersionAsync(
        CommandScope scope, UploadVersionCommand command, CancellationToken cancellationToken)
    {
        var loaded = await LoadHomeDocumentAsync(scope, command.CaseId, command.ViaLinkId, versionId: null, cancellationToken);
        if (loaded.Failure is { } failure)
        {
            return (null, null, failure);
        }

        var relationship = await RelationshipAsync(scope, loaded.Case!, false, cancellationToken);
        if (scope.Refuse(BusinessAction.UploadDocument, relationship, AuditEntityTypes.Document, loaded.Document!.Id, command.CaseId) is { } denied)
        {
            return (null, null, denied);
        }

        if (loaded.Document.Status != DocumentStatus.Active)
        {
            return (null, null, Rule("document.not_active"));
        }

        if (command.Placement is { } placement)
        {
            if (string.IsNullOrWhiteSpace(command.PlacementRoleCode))
            {
                return (null, null, Invalid("document.role_not_allowed_here", "RoleCode"));
            }

            // New versions enter only through the home case, and so does their placement (§4.4 rule 5).
            if (await ValidatePlacementTargetAsync(scope, command.CaseId, placement, command.PlacementRoleCode, loaded.Document.Id, null, cancellationToken) is { } refused)
            {
                return (null, null, refused);
            }
        }

        return (loaded.Link, loaded.Document, null);
    }

    /// <summary>
    /// A document-level act — a new version, a withdrawal, a reinstatement, a metadata correction — is performed through
    /// a visible ACTIVE placement in the document's home case, and on a version that placement exposes (§4.4, §10.1).
    /// The document row is locked: these acts are serialised per document (ADR-027).
    /// </summary>
    private static async Task<(CaseRow? Case, LinkRow? Link, DocumentRow? Document, VersionRow? Version, CommandResult<Guid>? Failure)> LoadHomeDocumentAsync(
        CommandScope scope, Guid caseId, Guid viaLinkId, Guid? versionId, CancellationToken cancellationToken)
    {
        var link = await DocumentSql.LoadLinkAsync(scope.UnitOfWork, viaLinkId, cancellationToken);
        var caseRow = await CaseSql.LoadAsync(scope.UnitOfWork, caseId, cancellationToken);
        if (link is null || caseRow is null || link.ContextCaseId != caseId || link.Status != DocumentLinkStatus.Active)
        {
            return (null, null, null, null, NotFound("document"));
        }

        if (scope.Refuse(BusinessAction.ViewDocument, await RelationshipAsync(scope, caseRow, false, cancellationToken), AuditEntityTypes.DocumentLink, link.Id, caseRow.Id) is { } hidden)
        {
            return (null, null, null, null, hidden);
        }

        var document = (await DocumentSql.LoadDocumentAsync(scope.UnitOfWork, link.DocumentId, forUpdate: true, cancellationToken))!;
        if (await DocumentSql.HomeCaseAsync(scope.UnitOfWork, document.Id, cancellationToken) != caseId)
        {
            return (null, null, null, null, Rule("document.not_home_case"));
        }

        VersionRow? version = null;
        if (versionId is { } requested)
        {
            version = await DocumentSql.LoadVersionAsync(scope.UnitOfWork, requested, cancellationToken);
            if (version is null || !DocumentSql.Exposes(link, version))
            {
                return (null, null, null, null, NotFound("document"));
            }
        }

        return (caseRow, link, document, version, null);
    }

    /// <summary>A source placement the actor can see that exposes the requested version.</summary>
    private static async Task<(VersionRow? Version, CommandResult<Guid>? Failure)> LoadExposedSourceAsync(
        CommandScope scope, Guid sourceLinkId, Guid versionId, CancellationToken cancellationToken)
    {
        var link = await DocumentSql.LoadLinkAsync(scope.UnitOfWork, sourceLinkId, cancellationToken);
        var version = await DocumentSql.LoadVersionAsync(scope.UnitOfWork, versionId, cancellationToken);
        if (link is null || version is null || !DocumentSql.Exposes(link, version))
        {
            return (null, NotFound("document"));
        }

        var sourceCase = (await CaseSql.LoadAsync(scope.UnitOfWork, link.ContextCaseId, cancellationToken))!;
        if (scope.Refuse(BusinessAction.ViewDocument, await RelationshipAsync(scope, sourceCase, false, cancellationToken), AuditEntityTypes.DocumentVersion, version.Id, sourceCase.Id) is { } hidden)
        {
            return (null, hidden);
        }

        return version.Status == DocumentVersionStatus.Withdrawn ? (null, Rule("document.version_withdrawn")) : (version, null);
    }

    private static async Task<Guid> InsertVersionAsync(
        CommandScope scope,
        string volumeCode,
        Guid documentId,
        int versionNo,
        Guid? supersedes,
        StagedContent bytes,
        DetectedFileType detected,
        UploadedContent content,
        DateOnly? documentDate,
        Guid caseId,
        CancellationToken cancellationToken)
    {
        var versionId = scope.NewId();
        var relativePath = ContentAddress.RelativePath(bytes.ContentHash);
        await scope.UnitOfWork.Command("""
                INSERT INTO rcs.document_version (id, document_id, version_no, original_filename, content_hash, hash_algorithm,
                                                  storage_volume_code, stored_relative_path, byte_size, mime_type, document_date,
                                                  uploaded_at, uploaded_by_user_id, supersedes_version_id, status, created_at, created_by_user_id)
                VALUES (@id, @document, @version_no, @filename, @hash, 'SHA256', @volume, @path, @size, @mime, @document_date,
                        @now, @actor, @supersedes, 'ACTIVE', @now, @actor)
                """)
            .With("id", versionId)
            .With("document", documentId)
            .With("version_no", versionNo)
            .With("filename", content.OriginalFileName)
            .With("hash", bytes.ContentHash)
            .With("volume", volumeCode)
            .With("path", relativePath)
            .WithLong("size", bytes.ByteSize)
            .With("mime", detected.MimeType)
            .With("document_date", documentDate)
            .With("now", scope.Now)
            .With("actor", scope.Actor.UserId)
            .With("supersedes", supersedes)
            .ExecuteAsync(cancellationToken);

        // §13.1: the upload event carries the hash, the name, the size, the detected type and any mismatch (§9.2).
        await scope.AuditAsync(new AuditEntry(AuditActionCodes.Upload, AuditEntityTypes.DocumentVersion, versionId, 1, caseId,
            After: new
            {
                document_id = documentId,
                version_no = versionNo,
                original_filename = content.OriginalFileName,
                byte_size = bytes.ByteSize,
                mime_type = detected.MimeType,
                declared_content_type = content.DeclaredContentType,
                extension = detected.OriginalExtension,
                extension_matches_content = detected.ExtensionMatchesContent,
                macro_enabled = detected.IsMacroEnabled,
                supersedes_version_id = supersedes,
                status = "ACTIVE",
            },
            DocumentHash: bytes.ContentHash), cancellationToken);

        return versionId;
    }

    private static async Task<Guid> InsertLinkAsync(
        CommandScope scope,
        Guid documentId,
        Guid? versionId,
        DocumentTarget target,
        string roleCode,
        bool isOrigin,
        Guid caseId,
        string? disclosureReason,
        CancellationToken cancellationToken)
    {
        var linkId = scope.NewId();
        var column = DocumentSql.TargetColumn(target.Kind);
        await scope.UnitOfWork.Command($"""
                INSERT INTO rcs.document_link (id, document_id, document_version_id, {column}, document_link_role_id, is_origin, ordinal,
                                               linked_by_user_id, linked_at, status, created_at, created_by_user_id)
                VALUES (@id, @document, @version, @target, (SELECT id FROM rcs.document_link_role WHERE code = @role), @is_origin,
                        (SELECT COALESCE(max(ordinal), -1) + 1 FROM rcs.document_link WHERE {column} = @target),
                        @actor, @now, 'ACTIVE', @now, @actor)
                """)
            .With("id", linkId)
            .With("document", documentId)
            .With("version", versionId)
            .With("target", target.Id)
            .With("role", roleCode)
            .With("is_origin", isOrigin)
            .With("actor", scope.Actor.UserId)
            .With("now", scope.Now)
            .ExecuteAsync(cancellationToken);

        await scope.AuditAsync(new AuditEntry(AuditActionCodes.Link, AuditEntityTypes.DocumentLink, linkId, 1, caseId,
            After: new
            {
                document_id = documentId,
                role = roleCode,
                target_type = DocumentSql.TargetEntityType(target.Kind),
                target_id = target.Id,
                pinned_version_id = versionId,
                is_origin = isOrigin,
                cross_case_disclosure = disclosureReason is not null,
            },
            ReasonNote: disclosureReason), cancellationToken);

        return linkId;
    }

    private async Task<Guid> PlaceIfAbsentAsync(CommandScope scope, Guid documentId, Guid versionId, DocumentTarget target, string roleCode, Guid caseId, CancellationToken cancellationToken)
    {
        var column = DocumentSql.TargetColumn(target.Kind);
        var existing = await scope.UnitOfWork.Command($"""
                SELECT id FROM rcs.document_link
                WHERE {column} = @target AND document_id = @document AND document_version_id = @version AND status = 'ACTIVE'
                LIMIT 1
                """)
            .With("target", target.Id).With("document", documentId).With("version", versionId)
            .ScalarAsync<Guid?>(cancellationToken);
        return existing ?? await InsertLinkAsync(scope, documentId, versionId, target, roleCode, false, caseId, null, cancellationToken);
    }

    private static async Task<bool> AlreadyPlacedAsync(CommandScope scope, Guid documentId, Guid versionId, DocumentTarget target, CancellationToken cancellationToken) =>
        await scope.UnitOfWork.Command($"""
                SELECT EXISTS (SELECT 1 FROM rcs.document_link
                               WHERE {DocumentSql.TargetColumn(target.Kind)} = @target AND document_id = @document
                                 AND (document_version_id = @version OR document_version_id IS NULL) AND status = 'ACTIVE')
                """)
            .With("target", target.Id).With("document", documentId).With("version", versionId)
            .ScalarAsync<bool>(cancellationToken);

    private static async Task<Guid> InsertEvidenceAsync(CommandScope scope, Guid requirementId, Guid documentId, Guid versionId, string? note, Guid caseId, CancellationToken cancellationToken)
    {
        var evidenceId = scope.NewId();
        await scope.UnitOfWork.Command("""
                INSERT INTO rcs.requirement_evidence (id, requirement_id, evidence_type, document_id, document_version_id, note, is_primary,
                                                      recorded_by_user_id, recorded_at, status, created_by_user_id)
                VALUES (@id, @requirement, 'DOCUMENT', @document, @version, @note, false, @actor, @now, 'ACTIVE', @actor)
                """)
            .With("id", evidenceId)
            .With("requirement", requirementId)
            .With("document", documentId)
            .With("version", versionId)
            .With("note", note)
            .With("actor", scope.Actor.UserId)
            .With("now", scope.Now)
            .ExecuteAsync(cancellationToken);

        await scope.AuditAsync(new AuditEntry(AuditActionCodes.Link, AuditEntityTypes.RequirementEvidence, evidenceId, 1, caseId,
            After: new { requirement_id = requirementId, evidence_type = "DOCUMENT", document_id = documentId, document_version_id = versionId }), cancellationToken);
        return evidenceId;
    }

    private static async Task<bool> RemoveLinkRowAsync(CommandScope scope, Guid linkId, int rowVersion, string reason, Guid caseId, ActorKind actorKind, CancellationToken cancellationToken)
    {
        var version = await scope.UnitOfWork.Command("""
                UPDATE rcs.document_link
                SET status = 'REMOVED', removed_at = @now, removed_by_user_id = @actor, removal_reason_note = @reason,
                    updated_at = @now, updated_by_user_id = @actor, row_version = row_version + 1
                WHERE id = @id AND row_version = @row_version AND status = 'ACTIVE'
                RETURNING row_version
                """)
            .With("now", scope.Now).With("actor", scope.Actor.UserId).With("reason", reason)
            .With("id", linkId).With("row_version", rowVersion)
            .ScalarAsync<int?>(cancellationToken);
        if (version is null)
        {
            return false;
        }

        await scope.AuditAsync(new AuditEntry(AuditActionCodes.Unlink, AuditEntityTypes.DocumentLink, linkId, version, caseId,
            Before: new { status = "ACTIVE" }, After: new { status = "REMOVED" }, ReasonNote: reason, ActorKind: actorKind), cancellationToken);
        return true;
    }

    /// <summary>Freezes the floating placements of a document in one case to its ACTIVE version, once (§4.4 "Freezing").</summary>
    private static async Task FreezeFloatingAsync(CommandScope scope, Guid documentId, Guid caseId, CancellationToken cancellationToken)
    {
        var current = (await DocumentSql.VersionsOfDocumentAsync(scope.UnitOfWork, documentId, cancellationToken))
            .FirstOrDefault(version => version.Status == DocumentVersionStatus.Active);
        foreach (var link in (await DocumentSql.ActiveLinksOfDocumentAsync(scope.UnitOfWork, documentId, cancellationToken))
                     .Where(link => link.VersionId is null && link.ContextCaseId == caseId))
        {
            if (current is null)
            {
                await RemoveLinkRowAsync(scope, link.Id, link.RowVersion, "home case moved; no current version to freeze", caseId, ActorKind.System, cancellationToken);
                continue;
            }

            var version = await scope.UnitOfWork.Command("""
                    UPDATE rcs.document_link
                    SET document_version_id = @version, updated_at = @now, updated_by_user_id = @actor, row_version = row_version + 1
                    WHERE id = @id AND document_version_id IS NULL
                    RETURNING row_version
                    """)
                .With("version", current.Id).With("now", scope.Now).With("actor", scope.Actor.UserId).With("id", link.Id)
                .ScalarAsync<int?>(cancellationToken);
            await scope.AuditAsync(new AuditEntry(AuditActionCodes.Update, AuditEntityTypes.DocumentLink, link.Id, version, caseId,
                Before: new { pinned_version_id = (Guid?)null }, After: new { pinned_version_id = current.Id }, ActorKind: ActorKind.System), cancellationToken);
        }
    }

    private static async Task ReinstateRowAsync(CommandScope scope, VersionRow version, string reason, Guid? afterWithdrawalOf, Guid caseId, CancellationToken cancellationToken)
    {
        var rowVersion = await scope.UnitOfWork.Command("""
                UPDATE rcs.document_version
                SET status = 'ACTIVE', updated_at = @now, updated_by_user_id = @actor, row_version = row_version + 1
                WHERE id = @id AND status = 'SUPERSEDED'
                RETURNING row_version
                """)
            .With("now", scope.Now).With("actor", scope.Actor.UserId).With("id", version.Id)
            .ScalarAsync<int?>(cancellationToken) ?? throw new InvalidOperationException("The version changed under the document lock.");

        // A STATE_CHANGE with a mandatory reason; no UPLOAD — nothing was uploaded (§13.1).
        await scope.AuditAsync(new AuditEntry(AuditActionCodes.StateChange, AuditEntityTypes.DocumentVersion, version.Id, rowVersion, caseId,
            Before: new { status = "SUPERSEDED" },
            After: new { status = "ACTIVE", reinstated = true, version_no = version.VersionNo, follows_withdrawal_of = afterWithdrawalOf },
            ReasonNote: reason), cancellationToken);
    }

    private async Task<CommandResult<UploadOutcome>> OutcomeOfLinkAsync(Guid linkId, CancellationToken cancellationToken) =>
        await ReadAsync(async unitOfWork =>
        {
            var link = await DocumentSql.LoadLinkAsync(unitOfWork, linkId, cancellationToken);
            return link is null
                ? CommandResult<UploadOutcome>.Failure(CommandErrorKind.NotFound, "notfound.document")
                : CommandResult<UploadOutcome>.Success(new UploadOutcome(link.DocumentId, link.VersionId ?? Guid.Empty, link.Id, false));
        });

    private async Task<CommandResult<UploadOutcome>> OutcomeOfVersionAsync(Guid versionId, CancellationToken cancellationToken) =>
        await ReadAsync(async unitOfWork =>
        {
            var version = await DocumentSql.LoadVersionAsync(unitOfWork, versionId, cancellationToken);
            return version is null
                ? CommandResult<UploadOutcome>.Failure(CommandErrorKind.NotFound, "notfound.document")
                : CommandResult<UploadOutcome>.Success(new UploadOutcome(version.DocumentId, version.Id, null, false));
        });

    private async Task<T> ReadAsync<T>(Func<PostgresUnitOfWork, Task<T>> read)
    {
        await using var unitOfWork = (PostgresUnitOfWork)await unitOfWorkFactory.BeginAsync();
        return await read(unitOfWork);
    }

    private static async Task<Rcs.Domain.Authorization.CaseRelationship> RelationshipAsync(CommandScope scope, CaseRow caseRow, bool ownWithoutDependents, CancellationToken cancellationToken) =>
        new(caseRow.IsRestricted,
            await CaseSql.ActorIsAssignedAsync(scope.UnitOfWork, caseRow.Id, scope.Actor.UserId, scope.Now, cancellationToken),
            ActorCreatedTargetWithoutDependents: ownWithoutDependents);

    private static List<VersionFacts> Facts(IEnumerable<VersionRow> versions) =>
        versions.Select(version => new VersionFacts(version.Id, version.VersionNo, version.Status)).ToList();

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static CommandResult<Guid> NotFound(string what) => CommandResult<Guid>.Failure(CommandErrorKind.NotFound, $"notfound.{what}");

    private static CommandResult<Guid> Rule(string code) => CommandResult<Guid>.Failure(CommandErrorKind.RuleViolation, code);

    private static CommandResult<Guid> Invalid(string code, string field) => CommandResult<Guid>.Failure(CommandErrorKind.Validation, code, field);

    private static CommandResult<Guid> Conflict() => CommandResult<Guid>.Failure(CommandErrorKind.Conflict, "concurrency.changed_elsewhere");
}
