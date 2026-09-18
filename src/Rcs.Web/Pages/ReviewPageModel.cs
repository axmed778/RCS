using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Rcs.Application.Common;
using Rcs.Web.Review;
using Rcs.Web.Ui;

namespace Rcs.Web.Pages;

/// <summary>
/// Shared plumbing for the review pages: the current actor, localised text, and turning a command failure into
/// something the form can show. Pages never decide authorization themselves — they ask the same policy the command
/// will apply, and the command re-checks regardless (PERMISSIONS.md §27.1).
/// </summary>
public abstract class ReviewPageModel(CurrentActor actor, UiText text) : PageModel
{
    protected CurrentActor Actor { get; } = actor;

    protected UiText Text { get; } = text;

    protected bool HasActor => Actor.IsAvailable;

    /// <summary>Puts a command failure on the field it belongs to, or on the form as a whole.</summary>
    protected void ApplyError(CommandError? error)
    {
        if (error is null)
        {
            return;
        }

        var message = Text.Error(error);
        ModelState.AddModelError(error.Field is { Length: > 0 } field ? $"Input.{field}" : string.Empty, message);
    }

    protected IActionResult ToWorkspace(Guid caseId, string? fragment = null) =>
        Redirect($"/cases/{caseId}" + (fragment is null ? string.Empty : "#" + fragment));
}
