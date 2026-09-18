using Rcs.Domain.Vocabulary;
using Rcs.Domain.Workflow;

namespace Rcs.Domain.Documents;

/// <summary>The business object a <c>document_link</c> places a document in — one arm of the exclusive arc (DOCUMENT_MODEL.md §4.3).</summary>
public enum DocumentTargetKind
{
    Correspondence = 1,
    Requirement,
    Request,
    Response,
    FinalResult,
    Case,
}

/// <summary>One version of a document, as the version rules read it.</summary>
public sealed record VersionFacts(Guid Id, int VersionNo, DocumentVersionStatus Status);

/// <summary>
/// The rules of the document model that do not need the database: which roles a context accepts, when a placement must
/// pin, and the version lifecycle of DOCUMENT_MODEL.md §5.2 (ADR-014, ADR-027).
/// </summary>
public static class DocumentRules
{
    /// <summary>
    /// Which roles each context accepts (DOCUMENT_MODEL.md §3.1, §4.2). A letter holds its primary letter, attachments,
    /// annexes and filed supporting material; evidence belongs to a requirement; a decision holds its documents and
    /// annexes; a case holds only material that names itself supporting (L2).
    /// </summary>
    public static bool RoleFitsTarget(DocumentTargetKind target, string roleCode) => target switch
    {
        DocumentTargetKind.Correspondence => roleCode is DocumentLinkRoleCodes.PrimaryLetter or DocumentLinkRoleCodes.Attachment
            or DocumentLinkRoleCodes.Annex or DocumentLinkRoleCodes.Supporting,
        DocumentTargetKind.Requirement => roleCode is DocumentLinkRoleCodes.RequirementEvidence
            or DocumentLinkRoleCodes.Supporting or DocumentLinkRoleCodes.WorkingCopy,
        DocumentTargetKind.FinalResult => roleCode is DocumentLinkRoleCodes.FinalResultDocument or DocumentLinkRoleCodes.Annex,
        DocumentTargetKind.Request or DocumentTargetKind.Response or DocumentTargetKind.Case =>
            roleCode is DocumentLinkRoleCodes.Supporting or DocumentLinkRoleCodes.WorkingCopy,
        _ => false,
    };

    /// <summary>
    /// "Anything that records history pins; floating is a narrow, same-case working convenience" (§4.4). A placement
    /// may float only when it is a SUPPORTING / WORKING_COPY working placement inside the document's home case.
    /// Every letter file, all evidence and every final-result placement pin (L8, L9 — this build pins final-result
    /// documents from creation), and so does every placement outside the home case (L10).
    /// </summary>
    public static bool MayFloat(DocumentTargetKind target, string roleCode, bool isHomeCase) =>
        isHomeCase
        && target is DocumentTargetKind.Case or DocumentTargetKind.Request or DocumentTargetKind.Response or DocumentTargetKind.Requirement
        && roleCode is DocumentLinkRoleCodes.Supporting or DocumentLinkRoleCodes.WorkingCopy;

    /// <summary>
    /// A version to be withdrawn (§5.2): not already withdrawn, a reason given, and not pinned by a decision that was
    /// issued — a pinned version is immune to correction; a wrong decision is superseded instead (§8.4).
    /// </summary>
    public static RuleCheck CanWithdrawVersion(DocumentVersionStatus status, bool pinnedByIssuedDecision, string? reasonCode)
    {
        if (status == DocumentVersionStatus.Withdrawn)
        {
            return RuleCheck.Fail("document.version_already_withdrawn");
        }

        if (pinnedByIssuedDecision)
        {
            return RuleCheck.Fail("document.version_pinned_by_decision");
        }

        return string.IsNullOrWhiteSpace(reasonCode) ? RuleCheck.Fail("document.withdrawal_reason_required") : RuleCheck.Ok;
    }

    /// <summary>
    /// Reinstatement (ADR-027): only when the document has no ACTIVE version, only its newest version that is not
    /// WITHDRAWN, and only with a reason. It is the same row — nothing is re-uploaded.
    /// </summary>
    public static RuleCheck CanReinstate(IReadOnlyCollection<VersionFacts> versions, Guid versionId, string? reason)
    {
        ArgumentNullException.ThrowIfNull(versions);
        if (versions.Any(version => version.Status == DocumentVersionStatus.Active))
        {
            return RuleCheck.Fail("document.has_current_version");
        }

        var candidate = ReinstatementCandidate(versions);
        if (candidate is null || candidate.Id != versionId)
        {
            return RuleCheck.Fail("document.not_reinstatable");
        }

        return string.IsNullOrWhiteSpace(reason) ? RuleCheck.Fail("document.reinstatement_reason_required") : RuleCheck.Ok;
    }

    /// <summary>The version reinstatement would make current, or null when there is none (or one is already current).</summary>
    public static VersionFacts? ReinstatementCandidate(IReadOnlyCollection<VersionFacts> versions)
    {
        ArgumentNullException.ThrowIfNull(versions);
        if (versions.Any(version => version.Status == DocumentVersionStatus.Active))
        {
            return null;
        }

        return versions
            .Where(version => version.Status != DocumentVersionStatus.Withdrawn)
            .OrderByDescending(version => version.VersionNo)
            .FirstOrDefault();
    }

    /// <summary>
    /// Plain removal of a placement (§8.2). An origin placement is never simply removed — it fixes the home case and
    /// is moved instead; the last ACTIVE placement of a document is withdrawn with the document, so no active document
    /// is ever left without context (L6); evidence is retracted, not unlinked; and an issued decision's documents are
    /// changed only by superseding the decision (§8.4).
    /// </summary>
    public static RuleCheck CanRemoveLink(
        DocumentLinkStatus status,
        string roleCode,
        bool isOrigin,
        int otherActiveLinks,
        FinalResultStatus? finalResultStatus,
        string? reason)
    {
        if (status != DocumentLinkStatus.Active)
        {
            return RuleCheck.Fail("document.link_not_active");
        }

        if (roleCode == DocumentLinkRoleCodes.RequirementEvidence)
        {
            return RuleCheck.Fail("document.evidence_link_retract_instead");
        }

        if (finalResultStatus is { } decision && decision != FinalResultStatus.Draft)
        {
            return RuleCheck.Fail("document.version_pinned_by_decision");
        }

        if (isOrigin)
        {
            return RuleCheck.Fail("document.origin_link_move_instead");
        }

        if (otherActiveLinks == 0)
        {
            return RuleCheck.Fail("document.last_link_withdraw_instead");
        }

        return string.IsNullOrWhiteSpace(reason) ? RuleCheck.Fail("document.removal_reason_required") : RuleCheck.Ok;
    }
}
