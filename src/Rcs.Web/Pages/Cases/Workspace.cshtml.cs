using Microsoft.AspNetCore.Mvc;
using Rcs.Application.Cases;
using Rcs.Application.Workflow;
using Rcs.Web.Review;
using Rcs.Web.Ui;

namespace Rcs.Web.Pages.Cases;

/// <summary>
/// The case workspace (PROJECT.md §17): header, the original incoming letter, the workflow as a nested tree, and the
/// activity history. The two one-click transitions live here; everything that needs a reason has its own form page.
/// </summary>
public sealed class WorkspaceModel(ICaseQueries cases, IWorkflowService workflow, CurrentActor actor, UiText text) : ReviewPageModel(actor, text)
{
    [BindProperty(SupportsGet = true)]
    public Guid CaseId { get; set; }

    public CaseWorkspace? Workspace { get; private set; }

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
}
