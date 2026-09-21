using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Rcs.Application.Identity;
using Rcs.Web.Authentication;
using Rcs.Web.Ui;

namespace Rcs.Web.Pages;

/// <summary>
/// The sign-in page. Every failure — unknown user, wrong password, suspended account, expired temporary credential —
/// produces the same message (SECURITY.md §6.4); the difference is recorded in the audit trail, not shown to whoever
/// is typing, including when an account is locked.
/// </summary>
[AllowAnonymous]
public sealed class LoginModel(ILocalAuthenticationService authentication, UiText text) : PageModel
{
    public string? Username { get; private set; }

    public string? ReturnUrl { get; private set; }

    public string? Message { get; private set; }

    public void OnGet(string? returnUrl = null, string? reason = null)
    {
        ReturnUrl = Local(returnUrl);
        Message = reason switch
        {
            "signedout" => text["Auth.SignedOut"].Value,
            "expired" => text["Auth.SessionExpired"].Value,
            _ => null,
        };
    }

    public async Task<IActionResult> OnPostAsync(string? username, string? password, string? returnUrl)
    {
        ReturnUrl = Local(returnUrl);
        Username = username;
        var result = await authentication.SignInAsync(username ?? string.Empty, password ?? string.Empty, RcsAuthentication.ClientHost(HttpContext), HttpContext.RequestAborted);
        if (!result.Succeeded || result.Profile is null || result.SessionId is null)
        {
            Message = text["Auth.Failed"].Value;
            return Page();
        }

        await HttpContext.SignInAsync(
            RcsAuthentication.Scheme,
            RcsAuthentication.PrincipalFor(result.Profile, result.SessionId.Value),
            new AuthenticationProperties { IsPersistent = false, AllowRefresh = false });

        return result.MustChangePassword ? Redirect("/account/password") : Redirect(ReturnUrl ?? "/");
    }

    /// <summary>Only same-site paths are followed after sign-in; anything else is ignored.</summary>
    private static string? Local(string? url) =>
        url is { Length: > 1 } && url[0] == '/' && url[1] != '/' && url[1] != '\\' ? url : null;
}
