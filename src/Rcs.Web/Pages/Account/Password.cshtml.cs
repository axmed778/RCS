using Microsoft.AspNetCore.Mvc;
using Rcs.Application.Identity;
using Rcs.Web.Authentication;
using Rcs.Web.Review;
using Rcs.Web.Ui;

namespace Rcs.Web.Pages.Account;

/// <summary>
/// A user replaces their own password. Nobody else can do it here — an administrative reset is a different act, by a
/// different person, on the administration screen (SECURITY.md §6.7). A successful change ends every other session of
/// the account and keeps this one.
/// </summary>
public sealed class PasswordModel(ILocalAuthenticationService authentication, CurrentActor actor, UiText text) : ReviewPageModel(actor, text)
{
    public bool MustChange { get; private set; }

    public string? Notice { get; private set; }

    public IActionResult OnGet()
    {
        if (!HasActor)
        {
            return Redirect("/login");
        }

        MustChange = HttpContext.Items.TryGetValue(RcsAuthentication.MustChangePasswordItem, out var flag) && flag is true;
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(string? currentPassword, string? newPassword, string? repeatPassword)
    {
        if (!HasActor)
        {
            return Redirect("/login");
        }

        MustChange = HttpContext.Items.TryGetValue(RcsAuthentication.MustChangePasswordItem, out var flag) && flag is true;
        if (!string.Equals(newPassword, repeatPassword, StringComparison.Ordinal))
        {
            ModelState.AddModelError(string.Empty, Text["Auth.PasswordsDiffer"].Value);
            return Page();
        }

        if (Actor.SessionId is not { } session)
        {
            // No server-side session: the review scaffold has no password to change.
            ModelState.AddModelError(string.Empty, Text["Auth.NoSession"].Value);
            return Page();
        }

        var result = await authentication.ChangePasswordAsync(
            Actor.Require.UserId, session, currentPassword ?? string.Empty, newPassword ?? string.Empty,
            RcsAuthentication.ClientHost(HttpContext), HttpContext.RequestAborted);
        if (!result.Succeeded)
        {
            ApplyError(result.Error);
            return Page();
        }

        HttpContext.Items[RcsAuthentication.MustChangePasswordItem] = false;
        Notice = Text["Auth.PasswordChanged"].Value;
        MustChange = false;
        return Page();
    }
}
