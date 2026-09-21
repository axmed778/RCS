using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Rcs.Application.Identity;
using Rcs.Web.Authentication;
using Rcs.Web.Review;

namespace Rcs.Web.Pages;

/// <summary>
/// Signing out revokes the session row first and drops the cookie second, so the session is dead on the server even
/// if the browser keeps the cookie (SECURITY.md §8.3). A GET does nothing: sign-out is a deliberate POST.
/// </summary>
public sealed class LogoutModel(ILocalAuthenticationService authentication, CurrentActor actor) : PageModel
{
    public IActionResult OnGet() => Redirect("/");

    public async Task<IActionResult> OnPostAsync()
    {
        if (actor.SessionId is { } session)
        {
            await authentication.SignOutAsync(session, RcsAuthentication.ClientHost(HttpContext), HttpContext.RequestAborted);
        }

        await HttpContext.SignOutAsync(RcsAuthentication.Scheme);
        return Redirect("/login?reason=signedout");
    }
}
