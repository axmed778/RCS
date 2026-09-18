using Microsoft.AspNetCore.Mvc;
using Rcs.Application.Cases;
using Rcs.Application.Documents;
using Rcs.Domain.Authorization;
using Rcs.Domain.Documents;
using Rcs.Domain.Vocabulary;
using Rcs.Application.Lifecycle;
using Rcs.Application.Workflow;
using Rcs.Web.Review;
using Rcs.Web.Ui;

namespace Rcs.Web.Pages.Cases;

/// <summary>
/// The case workspace (PROJECT.md §17): header, the original incoming letter, the workflow as a nested tree, the
/// final results, and the activity history. The one-click transitions live here; everything that needs a reason or a
/// confirmation has its own form page.
/// </summary>
public sealed class WorkspaceModel(
    ICaseQueries cases,
    ILifecycleQueries lifecycleQueries,
    IWorkflowService workflow,
    ICaseLifecycleService lifecycle,
    IDocumentQueries documents,
    CurrentActor actor,
    UiText text) : ReviewPageModel(actor, text)
{
    [BindProperty(SupportsGet = true)]
    public Guid CaseId { get; set; }

    public CaseWorkspace? Workspace { get; private set; }

    /// <summary>Every result of the case, newest first. Superseded and revoked ones stay readable (WORKFLOW.md §8.6).</summary>
    public IReadOnlyList<FinalResultView> FinalResults { get; private set; } = [];

    /// <summary>Every placement in the case's contexts, with the versions each exposes (DOCUMENT_MODEL.md §10.1).</summary>
    public CaseDocuments Documents { get; private set; } = CaseDocuments.Empty;

    /// <summary>A letter's first file is its primary letter; everything after it is an attachment unless chosen otherwise.</summary>
    public string LetterUploadRole(Guid letterId) =>
        Documents.For(DocumentTargetKind.Correspondence, letterId).Any(placement => placement.IsActive && placement.RoleCode == DocumentLinkRoleCodes.PrimaryLetter)
            ? DocumentLinkRoleCodes.Attachment
            : DocumentLinkRoleCodes.PrimaryLetter;

    /// <summary>The files of one context, ready for the inspector's list.</summary>
    public DocumentListView DocumentList(DocumentTargetKind kind, Guid id, string uploadRole, string? attachRole = null, bool canAdd = true, string? headingKey = null) =>
        new(CaseId, Documents.For(kind, id), new DocumentTarget(kind, id), uploadRole,
            canAdd && Workspace is not null && AuthorizationPolicy.Decide(Workspace.Actor, BusinessAction.UploadDocument, Workspace.Relationship).IsAllowed,
            attachRole, headingKey);

    [TempData]
    public string? ActionError { get; set; }

    public async Task<IActionResult> OnGetAsync()
    {
        if (!HasActor)
        {
            return Page();
        }

        var result = await cases.GetWorkspaceAsync(Actor.Context, CaseId, HttpContext.RequestAborted);
        if (!result.Succeeded)
        {
            return NotFound();
        }

        Workspace = result.Value;
        FinalResults = await lifecycleQueries.ListFinalResultsAsync(Actor.Context, CaseId, HttpContext.RequestAborted);
        Documents = (await documents.GetCaseDocumentsAsync(Actor.Context, CaseId, HttpContext.RequestAborted)).Value ?? CaseDocuments.Empty;
        return Page();
    }

    /// <summary>Q2 — a person starts work on an open requirement.</summary>
    public async Task<IActionResult> OnPostStartAsync(Guid requirementId, int rowVersion)
    {
        if (!HasActor)
        {
            return Page();
        }

        var result = await workflow.StartRequirementAsync(Actor.Context, new StartRequirementCommand(CaseId, requirementId, rowVersion), HttpContext.RequestAborted);
        if (!result.Succeeded)
        {
            ActionError = Text.Error(result.Error!);
        }

        return ToWorkspace(CaseId, "requirement-" + requirementId);
    }

    /// <summary>R4 — closure is always a person's act, under the frozen closure rule.</summary>
    public async Task<IActionResult> OnPostCloseRequestAsync(Guid requestId, int rowVersion, string? note)
    {
        if (!HasActor)
        {
            return Page();
        }

        var result = await workflow.CloseRequestAsync(Actor.Context, new CloseRequestCommand(CaseId, requestId, rowVersion, note), HttpContext.RequestAborted);
        if (!result.Succeeded)
        {
            ActionError = Text.Error(result.Error!);
        }

        return ToWorkspace(CaseId, "request-" + requestId);
    }

    /// <summary>F2 — the Head issues the result, recording the decision and the single approval (ADR-040).</summary>
    public async Task<IActionResult> OnPostIssueResultAsync(Guid finalResultId, int rowVersion, string? overrideNote, string? documentOverrideNote)
    {
        if (!HasActor)
        {
            return Page();
        }

        var result = await lifecycle.IssueFinalResultAsync(
            Actor.Context, new IssueFinalResultCommand(CaseId, finalResultId, rowVersion, overrideNote, documentOverrideNote), HttpContext.RequestAborted);
        if (!result.Succeeded)
        {
            ActionError = Text.Error(result.Error!);
        }

        return ToWorkspace(CaseId, "final-result-" + finalResultId);
    }

    /// <summary>F4 — the decision is withdrawn without a replacement, with a mandatory reason.</summary>
    public async Task<IActionResult> OnPostRevokeResultAsync(Guid finalResultId, int rowVersion, string? reason)
    {
        if (!HasActor)
        {
            return Page();
        }

        var result = await lifecycle.RevokeFinalResultAsync(
            Actor.Context, new RevokeFinalResultCommand(CaseId, finalResultId, rowVersion, reason ?? string.Empty), HttpContext.RequestAborted);
        if (!result.Succeeded)
        {
            ActionError = Text.Error(result.Error!);
        }

        return ToWorkspace(CaseId, "final-result-" + finalResultId);
    }
}
