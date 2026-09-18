using Microsoft.AspNetCore.Mvc;
using Rcs.Application.Cases;
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
    CurrentActor actor,
    UiText text) : ReviewPageModel(actor, text)
{
    [BindProperty(SupportsGet = true)]
    public Guid CaseId { get; set; }

    public CaseWorkspace? Workspace { get; private set; }

    /// <summary>Every result of the case, newest first. Superseded and revoked ones stay readable (WORKFLOW.md §8.6).</summary>
    public IReadOnlyList<FinalResultView> FinalResults { get; private set; } = [];

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
    public async Task<IActionResult> OnPostIssueResultAsync(Guid finalResultId, int rowVersion, string? overrideNote)
    {
        if (!HasActor)
        {
            return Page();
        }

        var result = await lifecycle.IssueFinalResultAsync(
            Actor.Context, new IssueFinalResultCommand(CaseId, finalResultId, rowVersion, overrideNote), HttpContext.RequestAborted);
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
