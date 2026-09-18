using Microsoft.AspNetCore.Mvc;
using Rcs.Application.Cases;
using Rcs.Application.Lifecycle;
using Rcs.Web.Review;
using Rcs.Web.Ui;

namespace Rcs.Web.Pages.Cases;

/// <summary>
/// T7 — reopening the same case (WORKFLOW.md §10). Never a copy and never a new case number: the dossier resumes,
/// its closure metadata is kept, and the reason becomes part of the permanent record.
/// </summary>
public sealed class ReopenModel(ICaseQueries cases, ICaseLifecycleService lifecycle, CurrentActor actor, UiText text)
    : ReviewPageModel(actor, text)
{
    public sealed class InputModel
    {
        public string? Reason { get; set; }

        public int RowVersion { get; set; }
    }

    [BindProperty(SupportsGet = true)]
    public Guid CaseId { get; set; }

    [BindProperty]
    public InputModel Input { get; set; } = new();

    public CaseWorkspace? Workspace { get; private set; }

    public async Task<IActionResult> OnGetAsync()
    {
        if (!HasActor)
        {
            return Page();
        }

        if (await LoadAsync() is { } failure)
        {
            return failure;
        }

        Input.RowVersion = Workspace!.Header.RowVersion;
        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        if (!HasActor)
        {
            return Page();
        }

        if (await LoadAsync() is { } failure)
        {
            return failure;
        }

        if (string.IsNullOrWhiteSpace(Input.Reason))
        {
            ModelState.AddModelError("Input.Reason", Text["Error.validation.required"]);
            return Page();
        }

        var result = await lifecycle.ReopenCaseAsync(
            Actor.Context, new ReopenCaseCommand(CaseId, Input.RowVersion, Input.Reason!), HttpContext.RequestAborted);
        if (!result.Succeeded)
        {
            ApplyError(result.Error);
            Input.RowVersion = Workspace!.Header.RowVersion;
            return Page();
        }

        return ToWorkspace(CaseId);
    }

    private async Task<IActionResult?> LoadAsync()
    {
        var workspace = await cases.GetWorkspaceAsync(Actor.Context, CaseId, HttpContext.RequestAborted);
        if (!workspace.Succeeded)
        {
            return NotFound();
        }

        Workspace = workspace.Value;
        return null;
    }
}
