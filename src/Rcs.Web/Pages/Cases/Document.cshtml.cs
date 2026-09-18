using Microsoft.AspNetCore.Mvc;
using Rcs.Application.Cases;
using Rcs.Application.Common;
using Rcs.Application.Documents;
using Rcs.Application.Identifiers;
using Rcs.Application.Idempotency;
using Rcs.Application.Lifecycle;
using Rcs.Application.Lookups;
using Rcs.Domain.Authorization;
using Rcs.Domain.Documents;
using Rcs.Domain.Vocabulary;
using Rcs.Web.Documents;
using Rcs.Web.Review;
using Rcs.Web.Ui;

namespace Rcs.Web.Pages.Cases;

/// <summary>
/// One placement of a document, seen from the case: the versions it exposes (and nothing else, DOCUMENT_MODEL.md §10.1),
/// and the ordinary clerical corrections of §8.2 — none of which needs database administration, and none of which
/// deletes anything. Every action is re-authorized by its command.
/// </summary>
public sealed class DocumentModel(
    ICaseQueries cases,
    ILifecycleQueries lifecycleQueries,
    IDocumentQueries documentQueries,
    IDocumentService documents,
    ILookupQueries lookups,
    IIdGenerator ids,
    CurrentActor actor,
    UiText text) : ReviewPageModel(actor, text)
{
    [BindProperty(SupportsGet = true)]
    public Guid CaseId { get; set; }

    [BindProperty(SupportsGet = true)]
    public Guid LinkId { get; set; }

    [TempData]
    public string? ActionError { get; set; }

    [TempData]
    public string? ActionNotice { get; set; }

    public CaseWorkspace? Workspace { get; private set; }

    public DocumentPlacementView? Placement { get; private set; }

    /// <summary>Other placements of the same document in this case — "also evidence for R1" (§19 principle 9).</summary>
    public IReadOnlyList<DocumentPlacementView> OtherPlacements { get; private set; } = [];

    public IReadOnlyList<DocumentContextOption> Contexts { get; private set; } = [];

    public IReadOnlyList<LookupItem> Kinds { get; private set; } = [];

    public IReadOnlyList<LookupItem> WithdrawalReasons { get; private set; } = [];

    public IReadOnlyList<CaseListItem> OtherCases { get; private set; } = [];

    public EvidenceView? Evidence { get; private set; }

    public Guid OperationId { get; private set; }

    public DocumentContextOption? ContextOf(DocumentTarget target) => Contexts.FirstOrDefault(option => option.Target == target);

    public bool May(BusinessAction action) =>
        Workspace is not null && AuthorizationPolicy.Decide(Workspace.Actor, action, Workspace.Relationship).IsAllowed;

    public bool IsChief => Workspace?.Actor.IsChiefOrAbove ?? false;

    /// <summary>
    /// Every version of this document the viewer can see through any ACTIVE placement in this case. A pinned placement
    /// exposes only its own version, so reinstatement must be judged on this union — otherwise a letter pinned to v1
    /// would offer to "reinstate" v1 while v2 is current.
    /// </summary>
    public IReadOnlyList<DocumentVersionView> VisibleVersions { get; private set; } = [];

    /// <summary>The version reinstatement would make current, when the document has none (ADR-027).</summary>
    public DocumentVersionView? ReinstatementCandidate =>
        DocumentRules.ReinstatementCandidate(VisibleVersions.Select(version => new VersionFacts(version.Id, version.VersionNo, version.Status)).ToArray()) is { } candidate
            ? VisibleVersions.First(version => version.Id == candidate.Id)
            : null;

    public async Task<IActionResult> OnGetAsync()
    {
        if (!HasActor)
        {
            return Page();
        }

        return await LoadAsync() ?? Page();
    }

    public Task<IActionResult> OnPostEditAsync(int documentRowVersion, string? title, string? kind, string? description, string? reference) =>
        ActAsync(() => documents.EditMetadataAsync(Actor.Context, new EditDocumentMetadataCommand(
            CaseId, LinkId, documentRowVersion, title ?? string.Empty, kind ?? string.Empty, description, reference), HttpContext.RequestAborted));

    public Task<IActionResult> OnPostWithdrawVersionAsync(Guid versionId, int rowVersion, string? reasonCode, string? note, bool reinstatePrevious, string? reinstatementReason) =>
        ActAsync(() => documents.WithdrawVersionAsync(Actor.Context, new WithdrawVersionCommand(
            CaseId, LinkId, versionId, rowVersion, reasonCode ?? string.Empty, note, reinstatePrevious, reinstatementReason), HttpContext.RequestAborted));

    public Task<IActionResult> OnPostReinstateAsync(Guid versionId, int rowVersion, string? reason) =>
        ActAsync(() => documents.ReinstateVersionAsync(Actor.Context, new ReinstateVersionCommand(
            CaseId, LinkId, versionId, rowVersion, reason ?? string.Empty), HttpContext.RequestAborted));

    public Task<IActionResult> OnPostRemoveAsync(int rowVersion, string? reason) =>
        ActAsync(() => documents.RemoveLinkAsync(Actor.Context, new RemoveLinkCommand(CaseId, LinkId, rowVersion, reason ?? string.Empty), HttpContext.RequestAborted));

    public Task<IActionResult> OnPostMoveAsync(int rowVersion, string? target, string? role, string? reason) =>
        ActAsync(() => DocumentTargetCodec.Parse(target) is { } parsed
            ? documents.MoveLinkAsync(Actor.Context, new MoveLinkCommand(CaseId, LinkId, rowVersion, CaseId, parsed, role ?? string.Empty, reason ?? string.Empty), HttpContext.RequestAborted)
            : Task.FromResult(CommandResult<Guid>.Failure(CommandErrorKind.Validation, "validation.required")), followNewLink: true);

    /// <summary>A file filed in the wrong case entirely (§8.2 case 2): moved as supporting material of the right case — a Chief's disclosure decision.</summary>
    public Task<IActionResult> OnPostMoveToCaseAsync(int rowVersion, Guid targetCaseId, string? reason) =>
        ActAsync(() => documents.MoveLinkAsync(Actor.Context, new MoveLinkCommand(
            CaseId, LinkId, rowVersion, targetCaseId, new DocumentTarget(DocumentTargetKind.Case, targetCaseId), DocumentLinkRoleCodes.Supporting, reason ?? string.Empty),
            HttpContext.RequestAborted), toCase: targetCaseId);

    public Task<IActionResult> OnPostWithdrawDocumentAsync(int documentRowVersion, string? reasonCode, string? note) =>
        ActAsync(() => documents.WithdrawDocumentAsync(Actor.Context, new WithdrawDocumentCommand(
            CaseId, LinkId, documentRowVersion, reasonCode ?? string.Empty, note), HttpContext.RequestAborted));

    public Task<IActionResult> OnPostRetractEvidenceAsync(Guid evidenceId, int rowVersion, string? note) =>
        ActAsync(() => documents.RetractEvidenceAsync(Actor.Context, new RetractEvidenceCommand(CaseId, evidenceId, rowVersion, note ?? string.Empty), HttpContext.RequestAborted));

    /// <summary>An explicit, pinned, reason-bearing disclosure into another case (ADR-016). Chief only.</summary>
    public Task<IActionResult> OnPostShareAsync(Guid versionId, Guid targetCaseId, string? reason, Guid operationId) =>
        ActAsync(() => documents.PlaceVersionAsync(Actor.Context, new PlaceVersionCommand(
            new OperationId(operationId == Guid.Empty ? ids.NewId() : operationId), targetCaseId, LinkId, versionId,
            new DocumentTarget(DocumentTargetKind.Case, targetCaseId), DocumentLinkRoleCodes.Supporting, reason), HttpContext.RequestAborted),
            notice: "Documents.Shared");

    private async Task<IActionResult> ActAsync(Func<Task<CommandResult<Guid>>> action, bool followNewLink = false, Guid? toCase = null, string? notice = null)
    {
        if (!HasActor)
        {
            return Page();
        }

        var result = await action();
        if (!result.Succeeded)
        {
            ActionError = Text.Error(result.Error!);
            return Redirect($"/cases/{CaseId}/documents/{LinkId}");
        }

        ActionNotice = notice is null ? Text["Documents.Saved"] : Text[notice];
        if (toCase is { } otherCase)
        {
            return Redirect($"/cases/{otherCase}/documents/{result.Value}");
        }

        return Redirect($"/cases/{CaseId}/documents/{(followNewLink ? result.Value : LinkId)}");
    }

    private async Task<IActionResult?> LoadAsync()
    {
        var workspace = await cases.GetWorkspaceAsync(Actor.Context, CaseId, HttpContext.RequestAborted);
        if (!workspace.Succeeded)
        {
            return NotFound();
        }

        Workspace = workspace.Value;
        var caseDocuments = (await documentQueries.GetCaseDocumentsAsync(Actor.Context, CaseId, HttpContext.RequestAborted)).Value;
        Placement = caseDocuments?.Find(LinkId);
        if (Placement is null)
        {
            return NotFound();
        }

        OtherPlacements = caseDocuments!.Placements
            .Where(placement => placement.DocumentId == Placement.DocumentId && placement.LinkId != LinkId && placement.IsActive)
            .ToArray();
        VisibleVersions = caseDocuments.Placements
            .Where(placement => placement.DocumentId == Placement.DocumentId && placement.IsActive)
            .SelectMany(placement => placement.Versions)
            .DistinctBy(version => version.Id)
            .ToArray();
        var results = await lifecycleQueries.ListFinalResultsAsync(Actor.Context, CaseId, HttpContext.RequestAborted);
        Contexts = DocumentContexts.Of(Workspace!, results, Text);
        Kinds = await lookups.ListActiveAsync(LookupKind.DocumentKind, HttpContext.RequestAborted);
        WithdrawalReasons = await lookups.ListActiveAsync(LookupKind.WithdrawalReason, HttpContext.RequestAborted);
        OtherCases = (await cases.ListAsync(Actor.Context, HttpContext.RequestAborted)).Where(item => item.Id != CaseId).ToArray();
        OperationId = ids.NewId();

        if (Placement.RoleCode == DocumentLinkRoleCodes.RequirementEvidence && Workspace!.FindRequirement(Placement.Target.Id) is { } requirement)
        {
            Evidence = requirement.Evidence.FirstOrDefault(item => item.DocumentVersionId == Placement.PinnedVersionId);
        }

        return null;
    }
}
