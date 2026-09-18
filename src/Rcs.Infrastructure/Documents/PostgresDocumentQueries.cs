using Microsoft.Extensions.Logging;
using Npgsql;
using Rcs.Application.Cases;
using Rcs.Application.Common;
using Rcs.Application.Documents;
using Rcs.Application.Identifiers;
using Rcs.Application.Identity;
using Rcs.Application.Persistence;
using Rcs.Domain.Authorization;
using Rcs.Domain.Documents;
using Rcs.Domain.Vocabulary;
using Rcs.Infrastructure.Audit;
using Rcs.Infrastructure.Cases;
using Rcs.Infrastructure.Identity;
using Rcs.Infrastructure.Persistence;

namespace Rcs.Infrastructure.Documents;

/// <summary>
/// The read side of documents. Everything is resolved through placements, and only versions a visible ACTIVE placement
/// exposes are ever returned — no count, number, filename or "newer version exists" hint for anything else
/// (DOCUMENT_MODEL.md §10.1, ADR-015).
/// </summary>
internal sealed class PostgresDocumentQueries(
    IUnitOfWorkFactory unitOfWorkFactory,
    LocalContentStore store,
    AuditWriter audit,
    IIdGenerator ids,
    TimeProvider timeProvider,
    ILogger<PostgresDocumentQueries> logger) : IDocumentQueries
{
    public async Task<CommandResult<CaseDocuments>> GetCaseDocumentsAsync(ActorContext actor, Guid caseId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        await using var unitOfWork = (PostgresUnitOfWork)await unitOfWorkFactory.BeginAsync(cancellationToken);
        if (await CanViewCaseAsync(unitOfWork, actor, caseId, cancellationToken) is null)
        {
            return CommandResult<CaseDocuments>.Failure(CommandErrorKind.NotFound, "notfound.case");
        }

        return CommandResult<CaseDocuments>.Success(new CaseDocuments(caseId, await PlacementsAsync(unitOfWork, caseId, cancellationToken)));
    }

    public async Task<IReadOnlyList<AttachableVersion>> ListAttachableAsync(ActorContext actor, Guid caseId, CancellationToken cancellationToken = default)
    {
        var documents = await GetCaseDocumentsAsync(actor, caseId, cancellationToken);
        if (!documents.Succeeded)
        {
            return [];
        }

        return documents.Value!.Placements
            .Where(placement => placement.IsActive)
            .SelectMany(placement => placement.Versions
                .Where(version => version.Status != DocumentVersionStatus.Withdrawn)
                .Select(version => new AttachableVersion(placement.LinkId, placement.DocumentId, placement.Title, placement.Target, placement.RoleCode, version)))
            .GroupBy(item => item.Version.Id)
            .Select(group => group.OrderByDescending(item => item.SourceRoleCode == DocumentLinkRoleCodes.PrimaryLetter).First())
            .OrderBy(item => item.Title, StringComparer.CurrentCulture)
            .ThenByDescending(item => item.Version.VersionNo)
            .ToArray();
    }

    public async Task<CommandResult<DocumentDownload>> OpenDownloadAsync(ActorContext actor, Guid caseId, Guid linkId, Guid versionId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        var notFound = CommandResult<DocumentDownload>.Failure(CommandErrorKind.NotFound, "notfound.document");
        var now = timeProvider.GetUtcNow();

        await using var unitOfWork = (PostgresUnitOfWork)await unitOfWorkFactory.BeginAsync(cancellationToken);
        var profile = await ActorStore.LoadAsync(unitOfWork, actor.UserId, now, cancellationToken);
        if (profile is null)
        {
            return notFound;
        }

        var link = await DocumentSql.LoadLinkAsync(unitOfWork, linkId, cancellationToken);
        var version = await DocumentSql.LoadVersionAsync(unitOfWork, versionId, cancellationToken);
        if (link is null || version is null)
        {
            // An identifier that names nothing: there is nothing to disclose and nothing to audit against.
            return notFound;
        }

        var caseRow = await CaseSql.LoadAsync(unitOfWork, link.ContextCaseId, cancellationToken);
        var relationship = caseRow is null
            ? null
            : new CaseRelationship(caseRow.IsRestricted, await CaseSql.ActorIsAssignedAsync(unitOfWork, caseRow.Id, profile.UserId, now, cancellationToken));
        var decision = AuthorizationPolicy.Decide(profile.Authority(), BusinessAction.ViewDocument, relationship);

        // §10.2: a removed link, a link that does not expose this version, a link reached through the wrong case, or a
        // case the actor cannot see — every one is refused the same way, as "not found", and recorded.
        var denial = !decision.IsAllowed ? decision.DenialCode
            : link.ContextCaseId != caseId ? "document.context_mismatch"
            : !DocumentSql.Exposes(link, version) ? "document.version_not_exposed"
            : null;
        if (denial is not null)
        {
            await audit.WriteAsync(unitOfWork, profile, actor.ClientHost, ids.NewId(), new AuditEntry(
                AuditActionCodes.PermissionDenied, AuditEntityTypes.DocumentVersion, version.Id, null, link.ContextCaseId,
                After: new { attempted_action = BusinessAction.ViewDocument.ToString(), denial_code = denial, link_id = link.Id }), cancellationToken);
            await unitOfWork.CommitAsync(cancellationToken);
            return notFound;
        }

        // §11.4: a broken object fails explicitly — never a silent 404, never wrong bytes served as if they were fine.
        var state = await store.CheckAsync(version.VolumeCode, version.RelativePath, version.ByteSize, expectedHash: null, cancellationToken);
        if (state != ObjectState.Intact)
        {
            logger.LogCritical("Integrity failure ({State}) on document version {VersionId} at {Path}.", state, version.Id, version.RelativePath);
            return CommandResult<DocumentDownload>.Failure(CommandErrorKind.RuleViolation, "document.integrity_failure");
        }

        var document = (await DocumentSql.LoadDocumentAsync(unitOfWork, version.DocumentId, forUpdate: false, cancellationToken))!;
        var stream = store.OpenRead(version.VolumeCode, version.RelativePath);
        try
        {
            // §13.3: the download event names the context it went through and the exact bytes delivered.
            await audit.WriteAsync(unitOfWork, profile, actor.ClientHost, ids.NewId(), new AuditEntry(
                AuditActionCodes.Download, AuditEntityTypes.DocumentVersion, version.Id, version.RowVersion, link.ContextCaseId,
                After: new
                {
                    document_id = version.DocumentId,
                    version_no = version.VersionNo,
                    link_id = link.Id,
                    role = link.RoleCode,
                    target_type = DocumentSql.TargetEntityType(link.Target.Kind),
                    target_id = link.Target.Id,
                },
                DocumentHash: version.ContentHash), cancellationToken);
            await unitOfWork.CommitAsync(cancellationToken);
        }
        catch
        {
            await stream.DisposeAsync();
            throw;
        }

        return CommandResult<DocumentDownload>.Success(new DocumentDownload(
            stream,
            FileTypePolicy.SafeDownloadFileName(document.Title, version.OriginalFileName, version.MimeType),
            version.ByteSize));
    }

    // ------------------------------------------------------------------ pieces

    private async Task<ActorProfile?> CanViewCaseAsync(PostgresUnitOfWork unitOfWork, ActorContext actor, Guid caseId, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var profile = await ActorStore.LoadAsync(unitOfWork, actor.UserId, now, cancellationToken);
        var caseRow = await CaseSql.LoadAsync(unitOfWork, caseId, cancellationToken);
        if (profile is null || caseRow is null)
        {
            return null;
        }

        var relationship = new CaseRelationship(caseRow.IsRestricted, await CaseSql.ActorIsAssignedAsync(unitOfWork, caseId, profile.UserId, now, cancellationToken));
        return AuthorizationPolicy.Decide(profile.Authority(), BusinessAction.ViewDocument, relationship).IsAllowed ? profile : null;
    }

    private sealed record PlacementRow(
        Guid LinkId,
        Guid DocumentId,
        Guid? PinnedVersionId,
        DocumentTarget Target,
        string RoleCode,
        bool IsOrigin,
        short? Ordinal,
        DateTimeOffset LinkedAt,
        UserRef LinkedBy,
        DocumentLinkStatus Status,
        DateTimeOffset? RemovedAt,
        UserRef? RemovedBy,
        string? RemovalReason,
        int RowVersion,
        string Title,
        string KindCode,
        string? Description,
        string? DocumentReference,
        DocumentStatus DocumentStatus,
        int DocumentRowVersion,
        Guid? HomeCaseId);

    private static async Task<IReadOnlyList<DocumentPlacementView>> PlacementsAsync(PostgresUnitOfWork unitOfWork, Guid caseId, CancellationToken cancellationToken)
    {
        var rows = await unitOfWork.Command("""
                SELECT l.id, l.document_id, l.document_version_id, l.correspondence_id, l.requirement_id, l.request_id, l.response_id,
                       l.final_result_id, l.case_id, r.code AS role_code, l.is_origin, l.ordinal, l.linked_at, l.status, l.removed_at,
                       l.removal_reason_note, l.row_version,
                       lu.id AS linked_by_id, lu.display_name AS linked_by_name, ru.id AS removed_by_id, ru.display_name AS removed_by_name,
                       d.title, k.code AS kind_code, d.description, d.document_reference, d.status AS document_status,
                       d.row_version AS document_row_version,
                       (SELECT hc.context_case_id
                        FROM rcs.document_link AS o JOIN rcs.document_link_context AS hc ON hc.document_link_id = o.id
                        WHERE o.document_id = l.document_id AND o.status = 'ACTIVE' AND o.is_origin) AS home_case_id
                FROM rcs.document_link AS l
                JOIN rcs.document_link_context AS ctx ON ctx.document_link_id = l.id
                JOIN rcs.document_link_role AS r ON r.id = l.document_link_role_id
                JOIN rcs.document AS d ON d.id = l.document_id
                JOIN rcs.document_kind AS k ON k.id = d.document_kind_id
                JOIN rcs.app_user AS lu ON lu.id = l.linked_by_user_id
                LEFT JOIN rcs.app_user AS ru ON ru.id = l.removed_by_user_id
                WHERE ctx.context_case_id = @case
                ORDER BY l.linked_at, l.id
                """)
            .With("case", caseId)
            .ListAsync(reader => new PlacementRow(
                reader.Uuid("id"),
                reader.Uuid("document_id"),
                reader.UuidOrNull("document_version_id"),
                DocumentSql.TargetOf(reader),
                reader.Text("role_code"),
                reader.Bool("is_origin"),
                reader.IsDBNull(reader.GetOrdinal("ordinal")) ? null : reader.GetInt16(reader.GetOrdinal("ordinal")),
                reader.Instant("linked_at"),
                new UserRef(reader.Uuid("linked_by_id"), reader.Text("linked_by_name")),
                VocabularyCodes.FromCode<DocumentLinkStatus>(reader.Text("status")),
                reader.InstantOrNull("removed_at"),
                reader.UuidOrNull("removed_by_id") is { } removedBy ? new UserRef(removedBy, reader.Text("removed_by_name")) : null,
                reader.TextOrNull("removal_reason_note"),
                reader.Int("row_version"),
                reader.Text("title"),
                reader.Text("kind_code"),
                reader.TextOrNull("description"),
                reader.TextOrNull("document_reference"),
                VocabularyCodes.FromCode<DocumentStatus>(reader.Text("document_status")),
                reader.Int("document_row_version"),
                reader.UuidOrNull("home_case_id")), cancellationToken);

        if (rows.Count == 0)
        {
            return [];
        }

        // Only exposing placements contribute versions: a REMOVED placement is history and exposes nothing (§10.2).
        var active = rows.Where(row => row.Status == DocumentLinkStatus.Active).ToArray();
        var pinned = active.Where(row => row.PinnedVersionId is not null).Select(row => row.PinnedVersionId!.Value).Distinct().ToArray();
        var floating = active.Where(row => row.PinnedVersionId is null).Select(row => row.DocumentId).Distinct().ToArray();
        var versions = await unitOfWork.Command("""
                SELECT v.id, v.document_id, v.version_no, v.original_filename, v.byte_size, v.mime_type, v.document_date,
                       v.uploaded_at, v.status, v.row_version, v.withdrawal_note, v.withdrawn_at,
                       uu.id AS uploaded_by_id, uu.display_name AS uploaded_by_name,
                       wu.id AS withdrawn_by_id, wu.display_name AS withdrawn_by_name, wr.code AS withdrawal_reason_code
                FROM rcs.document_version AS v
                JOIN rcs.app_user AS uu ON uu.id = v.uploaded_by_user_id
                LEFT JOIN rcs.app_user AS wu ON wu.id = v.withdrawn_by_user_id
                LEFT JOIN rcs.withdrawal_reason AS wr ON wr.id = v.withdrawal_reason_id
                WHERE v.id = ANY(@pinned) OR v.document_id = ANY(@floating)
                ORDER BY v.version_no DESC
                """)
            .WithIds("pinned", pinned)
            .WithIds("floating", floating)
            .ListAsync(reader => (DocumentId: reader.Uuid("document_id"), View: new DocumentVersionView(
                reader.Uuid("id"),
                reader.Int("version_no"),
                reader.Text("original_filename"),
                reader.Long("byte_size"),
                reader.Text("mime_type"),
                FileTypePolicy.FamilyOf(reader.Text("mime_type")),
                FileTypePolicy.IsMacroEnabled(reader.Text("mime_type")),
                reader.DateOrNull("document_date"),
                reader.Instant("uploaded_at"),
                new UserRef(reader.Uuid("uploaded_by_id"), reader.Text("uploaded_by_name")),
                VocabularyCodes.FromCode<DocumentVersionStatus>(reader.Text("status")),
                reader.Int("row_version"),
                reader.TextOrNull("withdrawal_reason_code"),
                reader.TextOrNull("withdrawal_note"),
                reader.InstantOrNull("withdrawn_at"),
                reader.UuidOrNull("withdrawn_by_id") is { } withdrawnBy ? new UserRef(withdrawnBy, reader.Text("withdrawn_by_name")) : null)), cancellationToken);

        var byId = versions.ToDictionary(version => version.View.Id, version => version.View);
        var byDocument = versions.ToLookup(version => version.DocumentId, version => version.View);

        return rows.Select(row =>
        {
            IReadOnlyList<DocumentVersionView> exposed = row.Status != DocumentLinkStatus.Active ? []
                : row.PinnedVersionId is { } pin ? (byId.TryGetValue(pin, out var one) ? [one] : [])
                : byDocument[row.DocumentId].ToArray();
            return new DocumentPlacementView(
                row.LinkId,
                row.DocumentId,
                row.Title,
                row.KindCode,
                row.Description,
                row.DocumentReference,
                row.DocumentStatus,
                row.DocumentRowVersion,
                row.Target,
                row.RoleCode,
                row.IsOrigin,
                row.PinnedVersionId,
                row.Ordinal,
                row.LinkedAt,
                row.LinkedBy,
                row.Status,
                row.RemovedAt,
                row.RemovedBy,
                row.RemovalReason,
                row.RowVersion,
                row.HomeCaseId == caseId,
                exposed);
        }).ToArray();
    }
}
