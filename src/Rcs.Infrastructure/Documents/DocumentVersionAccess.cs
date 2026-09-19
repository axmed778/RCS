using Rcs.Application.Common;
using Rcs.Application.Identifiers;
using Rcs.Application.Identity;
using Rcs.Domain.Authorization;
using Rcs.Domain.Vocabulary;
using Rcs.Infrastructure.Audit;
using Rcs.Infrastructure.Cases;
using Rcs.Infrastructure.Identity;
using Rcs.Infrastructure.Persistence;

namespace Rcs.Infrastructure.Documents;

/// <summary>An authorized path to one exact version: who, through which placement, which version.</summary>
internal sealed record VersionAccess(ActorProfile Profile, LinkRow Link, VersionRow Version);

/// <summary>The outcome of the check: access, or a refusal already recorded in the unit of work (commit it), or nothing to record.</summary>
internal sealed record VersionAccessResult(VersionAccess? Access, bool DenialRecorded);

/// <summary>
/// The single per-request, per-version authorization of DOCUMENT_MODEL.md §10.1, shared by every path to a version's
/// bytes or to anything derived from them — the download and every preview endpoint (ADR-015, ADR-044). The placement
/// must be ACTIVE, lie in the case named in the request, expose exactly this version (pinned to it, or floating), and
/// the case must be visible to the actor at this moment. There is no document-wide shortcut: access to v1 through a
/// pinned placement says nothing about v2.
/// </summary>
internal static class DocumentVersionAccess
{
    public static async Task<VersionAccessResult> AuthorizeAsync(
        PostgresUnitOfWork unitOfWork,
        AuditWriter audit,
        IIdGenerator ids,
        ActorContext actor,
        DateTimeOffset now,
        Guid caseId,
        Guid linkId,
        Guid versionId,
        string attempted,
        CancellationToken cancellationToken)
    {
        var profile = await ActorStore.LoadAsync(unitOfWork, actor.UserId, now, cancellationToken);
        if (profile is null)
        {
            return new VersionAccessResult(null, false);
        }

        var link = await DocumentSql.LoadLinkAsync(unitOfWork, linkId, cancellationToken);
        var version = await DocumentSql.LoadVersionAsync(unitOfWork, versionId, cancellationToken);
        if (link is null || version is null)
        {
            // An identifier that names nothing: there is nothing to disclose and nothing to audit against.
            return new VersionAccessResult(null, false);
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
        if (denial is null)
        {
            return new VersionAccessResult(new VersionAccess(profile, link, version), false);
        }

        await audit.WriteAsync(unitOfWork, profile, actor.ClientHost, ids.NewId(), new AuditEntry(
            AuditActionCodes.PermissionDenied, AuditEntityTypes.DocumentVersion, version.Id, null, link.ContextCaseId,
            After: new { attempted_action = attempted, denial_code = denial, link_id = link.Id }), cancellationToken);
        return new VersionAccessResult(null, true);
    }
}
