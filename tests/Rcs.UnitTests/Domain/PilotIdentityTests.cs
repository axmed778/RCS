using Microsoft.Extensions.Options;
using Rcs.Domain.Authorization;
using Rcs.Domain.Identity;
using Rcs.Domain.Vocabulary;
using Rcs.Infrastructure.Configuration;
using Rcs.Infrastructure.Identity;

namespace Rcs.UnitTests.Domain;

/// <summary>Password rules, hashing and the administrative half of <c>can()</c> (SECURITY.md §6; PERMISSIONS.md §25.2).</summary>
public sealed class PilotIdentityTests
{
    private static readonly Argon2PasswordHasher Hasher = new(Options.Create(new LocalAuthenticationOptions
    {
        // The release minimum, not the pilot server's cost: these tests run thousands of derivations.
        Argon2MemoryKibibytes = 8192,
        Argon2Iterations = 2,
        Argon2Parallelism = 1,
    }));

    private static ActorAuthority Actor(params BusinessRole[] roles) =>
        new(Guid.Parse("01995c00-0001-7000-8000-0000000000aa"), UserStatus.Active, roles.ToHashSet());

    [Theory]
    [InlineData("correct horse battery staple", true)]
    [InlineData("qısa", false)]                       // shorter than 12
    [InlineData("password12345", false)]              // blocklisted substring
    [InlineData("aaaaaaaaaaaaaa", false)]             // no variety
    [InlineData("abcdefghijklm", false)]              // a straight run
    [InlineData("parol parol parol", false)]          // blocklisted, Azerbaijani
    public void ThePasswordPolicyIsLengthNotComposition(string password, bool acceptable) =>
        Assert.Equal(acceptable, PasswordPolicy.IsAcceptable(password, "pilot.user"));

    [Fact]
    public void APasswordContainingTheUsernameIsRefused() =>
        Assert.Equal(PasswordRejection.ContainsUsername, PasswordPolicy.Check("pilot.user is my key", "pilot.user"));

    [Fact]
    public void AHashIsArgon2idSaltedAndNeverThePasswordItself()
    {
        const string password = "möhkəm uzun keçid ifadəsi";
        var first = Hasher.Hash(password);
        var second = Hasher.Hash(password);

        Assert.StartsWith("$argon2id$v=19$m=8192,t=2,p=1$", first, StringComparison.Ordinal);
        Assert.DoesNotContain(password, first, StringComparison.Ordinal);
        Assert.NotEqual(first, second); // a fresh salt each time
        Assert.Equal("ARGON2ID", Hasher.Algorithm);
    }

    [Fact]
    public void CanonicalPhcOmitsPaddingAndLegacyPaddingStillVerifies()
    {
        const string password = "a long memorable phrase";
        var hash = Hasher.Hash(password);
        var parts = hash.Split('$');
        Assert.DoesNotContain("=", parts[4], StringComparison.Ordinal);
        Assert.DoesNotContain("=", parts[5], StringComparison.Ordinal);
        parts[4] = parts[4].PadRight((parts[4].Length + 3) / 4 * 4, '=');
        parts[5] = parts[5].PadRight((parts[5].Length + 3) / 4 * 4, '=');
        var legacy = string.Join('$', parts);
        Assert.True(Hasher.Verify(legacy, password).Verified);
        Assert.True(Hasher.Verify(legacy, password).NeedsRehash);
    }

    [Fact]
    public void RehashDoesNotDowngradeAnExistingWorkFactor()
    {
        var mixed = new Argon2PasswordHasher(Options.Create(new LocalAuthenticationOptions
        {
            Argon2MemoryKibibytes = 16384, Argon2Iterations = 2, Argon2Parallelism = 1,
        })).Hash("a long memorable phrase");
        Assert.True(Hasher.Verify(mixed, "a long memorable phrase").Verified);
        Assert.False(Hasher.Verify(mixed, "a long memorable phrase").NeedsRehash);
    }

    [Fact]
    public void VerificationAcceptsTheRightPasswordAndRejectsEverythingElse()
    {
        var hash = Hasher.Hash("möhkəm uzun keçid ifadəsi");

        Assert.True(Hasher.Verify(hash, "möhkəm uzun keçid ifadəsi").Verified);
        Assert.False(Hasher.Verify(hash, "möhkəm uzun keçid ifadəsİ").Verified);
        Assert.False(Hasher.Verify(hash, string.Empty).Verified);
        Assert.False(Hasher.Verify("not-a-hash", "möhkəm uzun keçid ifadəsi").Verified);
    }

    [Fact]
    public void AHashMadeWithWeakerParametersIsVerifiedAndFlaggedForRehashing()
    {
        var weak = new Argon2PasswordHasher(Options.Create(new LocalAuthenticationOptions
        {
            Argon2MemoryKibibytes = 8192,
            Argon2Iterations = 2,
            Argon2Parallelism = 1,
        })).Hash("möhkəm uzun keçid ifadəsi");

        var stronger = new Argon2PasswordHasher(Options.Create(new LocalAuthenticationOptions
        {
            Argon2MemoryKibibytes = 16384,
            Argon2Iterations = 3,
            Argon2Parallelism = 1,
        }));

        var (verified, needsRehash) = stronger.Verify(weak, "möhkəm uzun keçid ifadəsi");
        Assert.True(verified);
        Assert.True(needsRehash);
    }

    [Fact]
    public void AccountAdministrationIsTechAdminAndRoleGrantsAreTheHeadsAlone()
    {
        var techAdmin = Actor(BusinessRole.TechAdmin);
        var head = Actor(BusinessRole.Head);
        var chief = Actor(BusinessRole.Chief);

        // PERMISSIONS.md §25.2: the split is the control.
        Assert.True(AdministrationPolicy.Decide(techAdmin, AdministrationAction.CreateUser).IsAllowed);
        Assert.True(AdministrationPolicy.Decide(techAdmin, AdministrationAction.SetCredential).IsAllowed);
        Assert.True(AdministrationPolicy.Decide(techAdmin, AdministrationAction.SetUserStatus).IsAllowed);
        Assert.False(AdministrationPolicy.Decide(techAdmin, AdministrationAction.GrantRole).IsAllowed);

        Assert.True(AdministrationPolicy.Decide(head, AdministrationAction.GrantRole).IsAllowed);
        Assert.False(AdministrationPolicy.Decide(head, AdministrationAction.CreateUser).IsAllowed);

        // A Chief administers nothing at all, however much business authority they hold.
        Assert.False(AdministrationPolicy.Decide(chief, AdministrationAction.CreateUser).IsAllowed);
        Assert.False(AdministrationPolicy.Decide(chief, AdministrationAction.GrantRole).IsAllowed);
    }

    [Fact]
    public void TechAdminGainsNoBusinessAuthorityFromAdministeringAccounts()
    {
        var techAdmin = Actor(BusinessRole.TechAdmin);

        foreach (var action in Enum.GetValues<BusinessAction>())
        {
            Assert.False(AuthorizationPolicy.Decide(techAdmin, action, new CaseRelationship(false, true)).IsAllowed);
        }
    }

    [Fact]
    public void ASuspendedAdministratorAdministersNothing()
    {
        var suspended = new ActorAuthority(Guid.Parse("01995c00-0001-7000-8000-0000000000bb"), UserStatus.Suspended, new[] { BusinessRole.TechAdmin, BusinessRole.Head }.ToHashSet());

        Assert.False(AdministrationPolicy.Decide(suspended, AdministrationAction.CreateUser).IsAllowed);
        Assert.False(AdministrationPolicy.Decide(suspended, AdministrationAction.GrantRole).IsAllowed);
    }

    /// <summary>The pilot employee holds three real roles; nothing about that is a special case in the policy.</summary>
    [Fact]
    public void ThePilotUserPassesTheNormalChecksBecauseTheyHoldTheRealRoles()
    {
        var pilot = Actor(BusinessRole.Worker, BusinessRole.Chief, BusinessRole.Head);
        var relationship = new CaseRelationship(false, true);

        Assert.True(AuthorizationPolicy.Decide(pilot, BusinessAction.CreateCase, relationship).IsAllowed);
        Assert.True(AuthorizationPolicy.Decide(pilot, BusinessAction.CloseCase, relationship).IsAllowed);
        Assert.True(AuthorizationPolicy.Decide(pilot, BusinessAction.ReopenCase, relationship).IsAllowed);
        Assert.True(AuthorizationPolicy.Decide(pilot, BusinessAction.IssueFinalResult, relationship).IsAllowed);
        Assert.True(AuthorizationPolicy.Decide(pilot, BusinessAction.OverrideClosureGuard, relationship).IsAllowed);

        // And the pilot user is not an administrator: accounts stay with TechAdmin.
        Assert.False(AdministrationPolicy.Decide(pilot, AdministrationAction.CreateUser).IsAllowed);
    }
}
