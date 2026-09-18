using Microsoft.AspNetCore.Mvc;
using Rcs.Application.Cases;
using Rcs.Application.Lifecycle;
using Rcs.Application.Lookups;
using Rcs.Domain.Authorization;
using Rcs.Web.Review;
using Rcs.Web.Ui;

namespace Rcs.Web.Pages.Cases;

/// <summary>
/// Closing a case (WORKFLOW.md §9). The screen exists because closure is never automatic (§9.3) and because the
/// person closing must see what is still open before they do it (§9.2.1 obligation 1): the guards are shown as they
/// stand, the open requirements are named, and closing over unresolved non-blocking work is an explicit confirmation.
/// Nothing on this screen resolves anything — the requirements keep their true state either way (obligation 2).
/// </summary>
public sealed class CloseModel(
    ICaseQueries cases,
    ILifecycleQueries lifecycleQueries,
    ICaseLifecycleService lifecycle,
    ILookupQueries lookups,
    CurrentActor actor,
    UiText text) : ReviewPageModel(actor, text)
{
    /// <summary>The ordinary ending: the department finished and issued its decision.</summary>
    private const string DefaultClosureType = "COMPLETED";

    public sealed class InputModel
    {
        public string ClosureTypeCode { get; set; } = DefaultClosureType;

        public string? Note { get; set; }

        public bool AcknowledgeUnresolvedNonBlocking { get; set; }

        /// <summary>The Head's reason naming each overridden guard (§9.4). Mandatory when a guard fails.</summary>
        public string? OverrideNote { get; set; }

        public int RowVersion { get; set; }
    }

    [BindProperty(SupportsGet = true)]
    public Guid CaseId { get; set; }

    [BindProperty]
    public InputModel Input { get; set; } = new();

    public CaseWorkspace? Workspace { get; private set; }

    public ClosurePreview? Preview { get; private set; }

    public IReadOnlyList<LookupItem> ClosureTypes { get; private set; } = [];

    /// <summary>Whether this actor could override a failing guard. Display only — the command decides again.</summary>
    public bool MayOverride { get; private set; }

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

        Input.RowVersion = Preview!.RowVersion;
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

        var result = await lifecycle.CloseCaseAsync(Actor.Context, new CloseCaseCommand(
            CaseId,
            Input.RowVersion,
            Input.ClosureTypeCode,
            Input.Note,
            Input.AcknowledgeUnresolvedNonBlocking,
            Input.OverrideNote), HttpContext.RequestAborted);

        if (!result.Succeeded)
        {
            ApplyError(result.Error);

            // The guards are re-shown as they are now, not as they were when the page was first drawn.
            Input.RowVersion = Preview!.RowVersion;
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
        ClosureTypes = await lookups.ListActiveAsync(LookupKind.ClosureType, HttpContext.RequestAborted);
        MayOverride = AuthorizationPolicy.Decide(Workspace!.Actor, BusinessAction.OverrideClosureGuard, Workspace.Relationship).IsAllowed;

        var preview = await lifecycleQueries.GetClosurePreviewAsync(
            Actor.Context, CaseId, Input.ClosureTypeCode ?? DefaultClosureType, HttpContext.RequestAborted);
        if (!preview.Succeeded)
        {
            return NotFound();
        }

        Preview = preview.Value;
        return null;
    }
}
