using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;
using Rcs.Web.Review;

namespace Rcs.Web.Pages.Review;

/// <summary>
/// Switches the review build between its two synthetic identities — the Chief who does the case work and the Head
/// who approves a final result or overrides a closure guard (PERMISSIONS.md §23).
/// </summary>
/// <remarks>
/// This is a Development scaffold, not authentication and not a role escalation: the page is only reachable because
/// the review build is on, which the host refuses outside Development, and it accepts no name but the two configured
/// synthetic users. Whichever is chosen, every record and audit row carries that user's own id — the attribution
/// stays real, which is the whole point of having a second identity rather than a more powerful first one.
/// </remarks>
public sealed class ActorModel(IOptions<ReviewOptions> options) : PageModel
{
    private readonly ReviewOptions options = options.Value;

    public IActionResult OnPost(string username, string? returnUrl)
    {
        if (!options.SelectableUsernames.Contains(username, StringComparer.Ordinal))
        {
            return BadRequest();
        }

        Response.Cookies.Append(CurrentActor.SelectionCookie, username, new CookieOptions
        {
            HttpOnly = true,
            SameSite = SameSiteMode.Strict,
            IsEssential = true,
            Path = "/",
        });

        // Only a local path is followed, so the switch can never be turned into an open redirect.
        return Redirect(Url.IsLocalUrl(returnUrl) ? returnUrl! : "/");
    }
}
