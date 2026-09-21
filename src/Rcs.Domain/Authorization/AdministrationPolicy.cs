using Rcs.Domain.Vocabulary;

namespace Rcs.Domain.Authorization;

/// <summary>
/// Account and role administration (PERMISSIONS.md §25.2). These are deliberately NOT
/// <see cref="BusinessAction"/> values: business authority and account administration are different powers held by
/// different people, and <see cref="AuthorizationPolicy.Decide"/> denies everything to an actor without a business
/// role — which is exactly what TechAdmin is.
/// </summary>
public enum AdministrationAction
{
    /// <summary>Create an account. TechAdmin. A new account holds no role and can do nothing until a Head grants one.</summary>
    CreateUser = 1,

    /// <summary>Set or reset a local credential, and release a sign-in lock. TechAdmin (SECURITY.md §6.4, §6.7).</summary>
    SetCredential,

    /// <summary>Suspend, deactivate or reactivate an account, on a business instruction. TechAdmin.</summary>
    SetUserStatus,

    /// <summary>Grant or revoke any role, including TechAdmin. <b>Head only</b> — the rule that keeps a TechAdmin from minting authority.</summary>
    GrantRole,

    /// <summary>See the account list and who holds which role. TechAdmin (to administer) and Head (to decide grants).</summary>
    ViewUsers,
}

/// <summary>
/// The administrative half of <c>can()</c>. Separate from the business policy and just as strict: the split in
/// PERMISSIONS.md §25.2 is a control, not a convenience, so no caller may collapse the two.
/// </summary>
public static class AdministrationPolicy
{
    public static AuthorizationDecision Decide(ActorAuthority actor, AdministrationAction action)
    {
        ArgumentNullException.ThrowIfNull(actor);

        // A suspended or deactivated administrator administers nothing.
        if (actor.Status != UserStatus.Active)
        {
            return AuthorizationDecision.Deny("auth.actor_not_active");
        }

        var isTechAdmin = actor.Roles.Contains(BusinessRole.TechAdmin);
        var isHead = actor.Roles.Contains(BusinessRole.Head);

        var allowed = action switch
        {
            // Account existence and credentials: the technical administrator, who gains no business authority by it.
            AdministrationAction.CreateUser or AdministrationAction.SetCredential or AdministrationAction.SetUserStatus => isTechAdmin,

            // Every role grant carries granted_by, and that is always a Head (PERMISSIONS.md §25.2, consequence 2).
            AdministrationAction.GrantRole => isHead,

            AdministrationAction.ViewUsers => isTechAdmin || isHead,
            _ => false,
        };

        return allowed ? AuthorizationDecision.Allow : AuthorizationDecision.Deny("auth.not_permitted");
    }
}
