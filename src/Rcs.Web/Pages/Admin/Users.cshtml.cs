using Microsoft.AspNetCore.Mvc;
using Rcs.Application.Identity;
using Rcs.Domain.Authorization;
using Rcs.Domain.Vocabulary;
using Rcs.Web.Review;
using Rcs.Web.Ui;

namespace Rcs.Web.Pages.Admin;

/// <summary>
/// Account and role administration (PERMISSIONS.md §25.2). The page shows only what the signed-in person may do —
/// account actions to a TechAdmin, role grants to the Head — and the service re-checks every one of them, so hiding a
/// form is presentation, never the control itself (PERMISSIONS.md §27.1).
/// </summary>
public sealed class UsersModel(IUserAdministration administration, CurrentActor actor, UiText text) : ReviewPageModel(actor, text)
{
    public IReadOnlyList<UserAccountView> Users { get; private set; } = [];

    public bool MayAdministerAccounts { get; private set; }

    public bool MayGrantRoles { get; private set; }

    [TempData]
    public string? ActionError { get; set; }

    [TempData]
    public string? Notice { get; set; }

    /// <summary>Shown once after a reset, never stored: the administrator hands it over in person (SECURITY.md §6.7).</summary>
    public string? TemporaryPassword { get; set; }

    public async Task<IActionResult> OnGetAsync()
    {
        if (!HasActor)
        {
            return Redirect("/login");
        }

        var authority = Actor.Require.Authority();
        MayAdministerAccounts = AdministrationPolicy.Decide(authority, AdministrationAction.CreateUser).IsAllowed;
        MayGrantRoles = AdministrationPolicy.Decide(authority, AdministrationAction.GrantRole).IsAllowed;

        var result = await administration.ListAsync(Actor.Context, HttpContext.RequestAborted);
        if (!result.Succeeded)
        {
            return NotFound();
        }

        Users = result.Value!;
        return Page();
    }

    public async Task<IActionResult> OnPostCreateAsync(string? username, string? fullName, string? displayName, string? jobTitle) =>
        Done(await administration.CreateUserAsync(Actor.Context, new CreateUserCommand(username ?? string.Empty, fullName ?? string.Empty, displayName ?? string.Empty, jobTitle, null), HttpContext.RequestAborted),
            Text["Admin.UserCreated"].Value);

    public async Task<IActionResult> OnPostResetPasswordAsync(Guid userId)
    {
        if (!HasActor)
        {
            return Redirect("/login");
        }

        var result = await administration.ResetPasswordAsync(Actor.Context, userId, HttpContext.RequestAborted);
        if (result.Succeeded)
        {
            TemporaryPassword = result.Value;
            Notice = Text["Admin.PasswordReset"].Value;
            return await OnGetAsync();
        }
        else
        {
            ActionError = Text.Error(result.Error!);
        }

        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostStatusAsync(Guid userId, string status, string? reason, int rowVersion) =>
        Done(await administration.SetStatusAsync(Actor.Context, userId, VocabularyCodes.FromCode<UserStatus>(status), reason, rowVersion, HttpContext.RequestAborted),
            Text["Admin.StatusChanged"].Value);

    public async Task<IActionResult> OnPostReleaseLockAsync(Guid userId) =>
        Done(await administration.ReleaseLockAsync(Actor.Context, userId, HttpContext.RequestAborted), Text["Admin.LockReleased"].Value);

    public async Task<IActionResult> OnPostGrantAsync(Guid userId, string role) =>
        Done(await administration.GrantRoleAsync(Actor.Context, userId, VocabularyCodes.FromCode<BusinessRole>(role), HttpContext.RequestAborted),
            Text["Admin.RoleGranted"].Value);

    public async Task<IActionResult> OnPostRevokeAsync(Guid userId, string role, string? reason) =>
        Done(await administration.RevokeRoleAsync(Actor.Context, userId, VocabularyCodes.FromCode<BusinessRole>(role), reason ?? string.Empty, HttpContext.RequestAborted),
            Text["Admin.RoleRevoked"].Value);

    private IActionResult Done(Rcs.Application.Common.CommandResult<Guid> result, string notice)
    {
        if (!HasActor)
        {
            return Redirect("/login");
        }

        if (result.Succeeded)
        {
            Notice = notice;
        }
        else
        {
            ActionError = Text.Error(result.Error!);
        }

        return RedirectToPage();
    }
}
