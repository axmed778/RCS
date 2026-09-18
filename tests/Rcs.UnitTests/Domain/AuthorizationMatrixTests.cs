using Rcs.Domain.Authorization;
using Rcs.Domain.Vocabulary;

namespace Rcs.UnitTests.Domain;

/// <summary>
/// The rows of the PERMISSIONS.md §26 matrix that the first vertical slice implements, expressed as cases so the
/// document and the code cannot drift apart silently (ARCHITECTURE.md §18.2).
/// </summary>
public sealed class AuthorizationMatrixTests
{
    private static ActorAuthority Actor(params BusinessRole[] roles) =>
        new(Guid.Parse("01995c10-0001-7000-8000-000000000001"), UserStatus.Active, roles.ToHashSet());

    private static readonly CaseRelationship NotAssigned = new(CaseIsRestricted: false, ActorIsAssigned: false);
    private static readonly CaseRelationship Assigned = new(CaseIsRestricted: false, ActorIsAssigned: true);

    [Theory]
    // Registering official letters, the responses they carry and the requests they carried needs no assignment:
    // the absence rule (PERMISSIONS.md §21.3, §21.4, footnote ⁴).
    [InlineData(BusinessAction.RegisterOutgoingRequest, false, true)]
    [InlineData(BusinessAction.RegisterResponse, false, true)]
    [InlineData(BusinessAction.CreateCase, false, true)]
    // Case work needs an assignment for a Worker.
    [InlineData(BusinessAction.FulfillRequirement, false, false)]
    [InlineData(BusinessAction.FulfillRequirement, true, true)]
    [InlineData(BusinessAction.StartRequirement, true, true)]
    // Management decisions are never reachable by assignment (PERMISSIONS.md §21.5).
    [InlineData(BusinessAction.WaiveRequirement, true, false)]
    [InlineData(BusinessAction.FailRequirement, true, false)]
    [InlineData(BusinessAction.AssignCase, true, false)]
    public void WorkerPermissions(BusinessAction action, bool assigned, bool expected) =>
        Assert.Equal(expected, AuthorizationPolicy.Decide(Actor(BusinessRole.Worker), action, assigned ? Assigned : NotAssigned).IsAllowed);

    [Theory]
    [InlineData(BusinessAction.WaiveRequirement)]
    [InlineData(BusinessAction.VoidRequirement)]
    [InlineData(BusinessAction.FailRequirement)]
    [InlineData(BusinessAction.AssignCase)]
    [InlineData(BusinessAction.FulfillRequirement)]
    [InlineData(BusinessAction.CloseRequest)]
    public void ChiefActsOnAnyCaseWithoutBeingAssigned(BusinessAction action) =>
        Assert.True(AuthorizationPolicy.Decide(Actor(BusinessRole.Chief), action, NotAssigned).IsAllowed);

    [Theory]
    [InlineData(BusinessAction.ViewCase)]
    [InlineData(BusinessAction.CreateCase)]
    [InlineData(BusinessAction.RegisterResponse)]
    [InlineData(BusinessAction.WaiveRequirement)]
    [InlineData(BusinessAction.CreateOrganization)]
    public void TechAdminHasNoBusinessCapabilityAtAll(BusinessAction action)
    {
        var decision = AuthorizationPolicy.Decide(Actor(BusinessRole.TechAdmin), action, NotAssigned);

        Assert.False(decision.IsAllowed);
        Assert.Equal("auth.no_business_role", decision.DenialCode);
    }

    [Fact]
    public void AWorkerMayVoidTheirOwnMistakeButNotMakeABusinessJudgement()
    {
        var ownMistake = new CaseRelationship(false, false, ActorCreatedTargetWithoutDependents: true, VoidReasonCode: VoidReasonCodes.DataEntryError);
        var businessReason = new CaseRelationship(false, true, ActorCreatedTargetWithoutDependents: true, VoidReasonCode: VoidReasonCodes.NoLongerRequired);

        Assert.True(AuthorizationPolicy.Decide(Actor(BusinessRole.Worker), BusinessAction.VoidRequirement, ownMistake).IsAllowed);
        Assert.False(AuthorizationPolicy.Decide(Actor(BusinessRole.Worker), BusinessAction.VoidRequirement, businessReason).IsAllowed);
        Assert.True(AuthorizationPolicy.Decide(Actor(BusinessRole.Chief), BusinessAction.VoidRequirement, businessReason).IsAllowed);
    }

    [Fact]
    public void ARestrictedCaseIsInvisibleToAnUnassignedWorkerAndVisibleToAChief()
    {
        var restricted = new CaseRelationship(CaseIsRestricted: true, ActorIsAssigned: false);

        var worker = AuthorizationPolicy.Decide(Actor(BusinessRole.Worker), BusinessAction.ViewCase, restricted);
        Assert.False(worker.IsAllowed);
        Assert.Equal("auth.case_not_visible", worker.DenialCode);

        Assert.True(AuthorizationPolicy.Decide(Actor(BusinessRole.Worker), BusinessAction.ViewCase, restricted with { ActorIsAssigned = true }).IsAllowed);
        Assert.True(AuthorizationPolicy.Decide(Actor(BusinessRole.Chief), BusinessAction.ViewCase, restricted).IsAllowed);
        Assert.True(AuthorizationPolicy.Decide(Actor(BusinessRole.Head), BusinessAction.ViewCase, restricted).IsAllowed);
    }

    [Theory]
    [InlineData(UserStatus.Suspended)]
    [InlineData(UserStatus.Deactivated)]
    public void AnInactiveAccountCanDoNothing(UserStatus status)
    {
        var actor = new ActorAuthority(Guid.NewGuid(), status, new HashSet<BusinessRole> { BusinessRole.Head });

        var decision = AuthorizationPolicy.Decide(actor, BusinessAction.ViewCase, NotAssigned);

        Assert.False(decision.IsAllowed);
        Assert.Equal("auth.actor_not_active", decision.DenialCode);
    }
}
