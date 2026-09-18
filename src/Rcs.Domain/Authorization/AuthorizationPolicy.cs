using Rcs.Domain.Vocabulary;

namespace Rcs.Domain.Authorization;

/// <summary>The business actions the first vertical slice performs. Every one is decided by <see cref="AuthorizationPolicy"/>.</summary>
public enum BusinessAction
{
    ViewCase = 1,
    CreateCase,
    CreateOrganization,
    AssignCase,

    /// <summary>Registering an already-sent outgoing letter together with the request it carried (WORKFLOW.md R1b).</summary>
    RegisterOutgoingRequest,
    RegisterResponse,
    CreateRequirementFromResponse,
    StartRequirement,
    FulfillRequirement,
    WaiveRequirement,
    VoidRequirement,
    FailRequirement,
    CloseRequest,

    /// <summary>Starting or editing a DRAFT final result (WORKFLOW.md §8.1, F1).</summary>
    DraftFinalResult,

    /// <summary>
    /// Issuing a final result (F2). One act that records the decision and the approval, so it is the Head's
    /// alone — a Chief may draft and decide, never approve (ADR-040; PERMISSIONS.md §23).
    /// </summary>
    IssueFinalResult,

    /// <summary>Withdrawing an issued result without a replacement (F4) — Head, with a reason.</summary>
    RevokeFinalResult,

    CloseCase,

    /// <summary>Closing over a failing guard G1, G2, G3 or G5 — Head only, mandatory reason (WORKFLOW.md §9.4).</summary>
    OverrideClosureGuard,

    ReopenCase,
}

/// <summary>Who is acting, with roles evaluated at action time (PERMISSIONS.md §27.3).</summary>
public sealed record ActorAuthority(Guid UserId, UserStatus Status, IReadOnlySet<BusinessRole> Roles)
{
    public bool IsChiefOrAbove => Roles.Contains(BusinessRole.Chief) || Roles.Contains(BusinessRole.Head);

    /// <summary>TechAdmin alone carries no business capability (PERMISSIONS.md §18.1).</summary>
    public bool HasBusinessRole => Roles.Contains(BusinessRole.Worker) || IsChiefOrAbove;
}

/// <summary>Relationship facts about the object acted on (PERMISSIONS.md §21.2).</summary>
/// <param name="CaseIsRestricted">The case's restricted flag (PERMISSIONS.md §19).</param>
/// <param name="ActorIsAssigned">An ACTIVE assignment on the case covering now, any role including temporary cover.</param>
/// <param name="ActorRegisteredSourceResponse">The actor registered the response a requirement is raised from.</param>
/// <param name="ActorCreatedTargetWithoutDependents">The actor created the target row and nothing depends on it yet (§24.1).</param>
/// <param name="VoidReasonCode">For a void, the reason chosen.</param>
public sealed record CaseRelationship(
    bool CaseIsRestricted,
    bool ActorIsAssigned,
    bool ActorRegisteredSourceResponse = false,
    bool ActorCreatedTargetWithoutDependents = false,
    string? VoidReasonCode = null);

public sealed record AuthorizationDecision(bool IsAllowed, string? DenialCode)
{
    public static readonly AuthorizationDecision Allow = new(true, null);

    public static AuthorizationDecision Deny(string code) => new(false, code);
}

/// <summary>
/// The server-side <c>can()</c> of PERMISSIONS.md §14.2 for the actions of the first vertical slice: deny by
/// default, visibility before authority, roles as of now. Workflow-state guards are checked by the workflow
/// rules, not here.
/// </summary>
/// <remarks>
/// Restricted-case grants (<c>case_access_grant</c>) are not built yet, so a restricted case is visible only
/// to assigned users, Chief and Head. Where PERMISSIONS.md names no rule for an action, the assumption used is
/// stated on that action and marked PROVISIONAL.
/// </remarks>
public static class AuthorizationPolicy
{
    public static AuthorizationDecision Decide(ActorAuthority actor, BusinessAction action, CaseRelationship? relationship = null)
    {
        ArgumentNullException.ThrowIfNull(actor);

        if (actor.Status != UserStatus.Active)
        {
            return AuthorizationDecision.Deny("auth.actor_not_active");
        }

        if (!actor.HasBusinessRole)
        {
            return AuthorizationDecision.Deny("auth.no_business_role");
        }

        if (relationship is { CaseIsRestricted: true, ActorIsAssigned: false } && !actor.IsChiefOrAbove)
        {
            return AuthorizationDecision.Deny("auth.case_not_visible");
        }

        var assigned = relationship?.ActorIsAssigned ?? false;

        var allowed = action switch
        {
            BusinessAction.ViewCase => true,
            BusinessAction.CreateCase => true,

            // PROVISIONAL: PERMISSIONS.md §26 has no row for organization master data. WORKFLOW.md §2.1 step 3 has
            // the registering person create a missing organization first, so any business role may.
            BusinessAction.CreateOrganization => true,

            BusinessAction.AssignCase => actor.IsChiefOrAbove,

            // The absence rule: registering an official letter, and the request it carried, needs no assignment
            // (PERMISSIONS.md §21.3, §21.4, footnote ⁴).
            BusinessAction.RegisterOutgoingRequest => true,
            BusinessAction.RegisterResponse => true,

            // Assigned/cover, or the person registering that response (footnote ⁶).
            BusinessAction.CreateRequirementFromResponse =>
                actor.IsChiefOrAbove || assigned || (relationship?.ActorRegisteredSourceResponse ?? false),

            // Case work: assigned/cover (footnote ³). Starting work is the explicit form of WORKFLOW.md Q2.
            BusinessAction.FulfillRequirement or BusinessAction.StartRequirement => actor.IsChiefOrAbove || assigned,

            // PROVISIONAL: PERMISSIONS.md names no authority for request closure (WORKFLOW.md R4). Treated as case
            // work, like fulfilling a requirement.
            BusinessAction.CloseRequest => actor.IsChiefOrAbove || assigned,

            BusinessAction.WaiveRequirement or BusinessAction.FailRequirement => actor.IsChiefOrAbove,

            // PROVISIONAL: PERMISSIONS.md §23 lists no row for drafting a final result — only for approving,
            // overriding and revoking one. Drafting is treated as the Chief judgement the decision itself is.
            BusinessAction.DraftFinalResult => actor.IsChiefOrAbove,

            BusinessAction.CloseCase or BusinessAction.ReopenCase => actor.IsChiefOrAbove,

            // The Head's own acts (PERMISSIONS.md §23). IsChiefOrAbove is deliberately NOT used: a Chief who
            // could approve a final result or override a closure guard would empty both rules of meaning.
            BusinessAction.IssueFinalResult or BusinessAction.RevokeFinalResult or BusinessAction.OverrideClosureGuard =>
                actor.Roles.Contains(BusinessRole.Head),

            // Business reasons are a Chief judgement; a Worker may only correct their own entry (footnote ⁷).
            BusinessAction.VoidRequirement => actor.IsChiefOrAbove
                || (relationship is { VoidReasonCode: { } reason, ActorCreatedTargetWithoutDependents: true }
                    && VoidReasonCodes.IsCorrection(reason)),

            _ => false,
        };

        return allowed ? AuthorizationDecision.Allow : AuthorizationDecision.Deny("auth.action_not_permitted");
    }
}
