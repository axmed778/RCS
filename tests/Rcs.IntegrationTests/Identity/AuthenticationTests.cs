using Rcs.Application.Common;
using Rcs.Application.Identity;
using Rcs.Domain.Vocabulary;
using Rcs.IntegrationTests.TestSupport;

namespace Rcs.IntegrationTests.Identity;

/// <summary>
/// Local sign-in, sessions and account administration against a real database (SECURITY.md §6, §8; PERMISSIONS.md §25.2).
/// Every test drives the same services the host uses, so what passes here is what the pilot server will do.
/// </summary>
public sealed class AuthenticationTests : IAsyncLifetime
{
    private const string Password = "kifayət qədər uzun keçid ifadəsi";
    private SliceFixture fixture = null!;
    private Guid adminId;
    private Guid pilotId;

    public async ValueTask InitializeAsync()
    {
        // No demo data: this is the shape of a clean pilot database, bootstrapped from the console.
        fixture = await SliceFixture.CreateAsync(seedDemoData: false);
        var admin = await fixture.Bootstrap.CreateAdministratorAsync("tech.admin", "Texniki inzibatçı", null, Password);
        adminId = admin.Value!.UserId;
        var pilot = await fixture.Bootstrap.CreateUserAsync("pilot.user", "Pilot İstifadəçi", null, "Baş mütəxəssis", Password, "tech.admin");
        pilotId = pilot.Value!.UserId;
    }

    public async ValueTask DisposeAsync() => await fixture.DisposeAsync();

    private ActorContext Admin => new(adminId, "integration-test");

    private async Task<Guid> SignInAsync(string username = "pilot.user", string password = Password)
    {
        var result = await fixture.Authentication.SignInAsync(username, password, "10.0.0.5");
        Assert.True(result.Succeeded, result.Outcome.ToString());
        return result.SessionId!.Value;
    }

    [Fact]
    public async Task BootstrapCannotGrantOtherRolesOrReopenAfterHeadSuspension()
    {
        Assert.False((await fixture.Bootstrap.GrantRoleAsync("pilot.user", BusinessRole.Worker, "tech.admin")).Succeeded);
        Assert.False((await fixture.Bootstrap.CreateAdministratorAsync("second.admin", "Second", null, Password)).Succeeded);
        Assert.False((await fixture.Bootstrap.CreateAdministratorAsync("tech.admin", "Existing", null, Password)).Succeeded);
        Assert.True((await fixture.Bootstrap.GrantRoleAsync("pilot.user", BusinessRole.Head, "tech.admin")).Succeeded);
        var version = await fixture.ScalarAsync<int>($"SELECT row_version FROM rcs.app_user WHERE id = '{pilotId}'");
        Assert.True((await fixture.Administration.SetStatusAsync(Admin, pilotId, UserStatus.Suspended, "test", version)).Succeeded);
        Assert.False((await fixture.Bootstrap.GrantRoleAsync("tech.admin", BusinessRole.Head, "tech.admin")).Succeeded);
    }

    [Fact]
    public async Task ExpiredTemporaryCredentialRejectsAnExistingSessionAndPasswordChange()
    {
        var session = await SignInAsync();
        await fixture.Database.ExecuteAsync($"UPDATE rcs.user_credential SET expires_at = now() - interval '1 second' WHERE user_id = '{pilotId}'");
        Assert.Null(await fixture.Authentication.ValidateSessionAsync(session, "test"));
        Assert.Equal(SignInOutcome.CredentialExpired, (await fixture.Authentication.SignInAsync("pilot.user", Password, "test")).Outcome);
        Assert.False((await fixture.Authentication.ChangePasswordAsync(pilotId, session, Password, "a different long phrase", "test")).Succeeded);
    }

    [Fact]
    public async Task PasswordChangeRejectsRevokedOrUnrelatedSession()
    {
        var session = await SignInAsync();
        Assert.False((await fixture.Authentication.ChangePasswordAsync(pilotId, Guid.NewGuid(), Password, "a different long phrase", "test")).Succeeded);
        await fixture.Authentication.SignOutAsync(session, "test");
        Assert.False((await fixture.Authentication.ChangePasswordAsync(pilotId, session, Password, "a different long phrase", "test")).Succeeded);
    }

    [Fact]
    public async Task ExpiredLockStartsANewBoundedAttemptWindow()
    {
        await fixture.Database.ExecuteAsync($"UPDATE rcs.user_credential SET failed_attempt_count=5, locked_until=now()-interval '1 second' WHERE user_id='{pilotId}'");
        Assert.Equal(SignInOutcome.InvalidCredentials, (await fixture.Authentication.SignInAsync("pilot.user", "wrong", "test")).Outcome);
        Assert.Equal(1, await fixture.ScalarAsync<int>($"SELECT failed_attempt_count FROM rcs.user_credential WHERE user_id='{pilotId}'"));
        Assert.Equal(SignInOutcome.Succeeded, (await fixture.Authentication.SignInAsync("pilot.user", Password, "test")).Outcome);
    }

    [Fact]
    public async Task RuntimeCannotDeleteCredentialsOrSessionsAndMigrationOwnsThem()
    {
        Assert.False(await fixture.ScalarAsync<bool>("SELECT has_table_privilege('rcs_app','rcs.user_credential','DELETE')"));
        Assert.False(await fixture.ScalarAsync<bool>("SELECT has_table_privilege('rcs_app','rcs.user_session','DELETE')"));
        Assert.False(await fixture.ScalarAsync<bool>("SELECT has_column_privilege('rcs_app','rcs.user_session','user_id','UPDATE')"));
        Assert.Equal(2L, await fixture.ScalarAsync<long>("SELECT count(*) FROM pg_tables WHERE schemaname='rcs' AND tablename IN ('user_credential','user_session') AND tableowner='rcs_migrate'"));
    }

    [Fact]
    public async Task AValidPasswordSignsInAndAWrongOneDoesNot()
    {
        var success = await fixture.Authentication.SignInAsync("pilot.user", Password, "10.0.0.5");
        Assert.Equal(SignInOutcome.Succeeded, success.Outcome);
        Assert.Equal(pilotId, success.Profile!.UserId);
        Assert.True(success.MustChangePassword); // a console-set credential is always first-use

        var failure = await fixture.Authentication.SignInAsync("pilot.user", "səhv parol filan", "10.0.0.5");
        Assert.Equal(SignInOutcome.InvalidCredentials, failure.Outcome);
        Assert.Null(failure.SessionId);

        var unknown = await fixture.Authentication.SignInAsync("yoxdur", Password, "10.0.0.5");
        Assert.Equal(SignInOutcome.InvalidCredentials, unknown.Outcome);

        // Both the success and the failure are in the audit trail, under the real account.
        Assert.Equal(1L, await fixture.ScalarAsync<long>(
            $"SELECT count(*) FROM rcs.audit_event WHERE action_code = 'LOGIN' AND actor_user_id = '{pilotId}' AND after_state->>'outcome' = 'SUCCEEDED'"));
        Assert.Equal(1L, await fixture.ScalarAsync<long>(
            $"SELECT count(*) FROM rcs.audit_event WHERE action_code = 'LOGIN' AND actor_user_id = '{pilotId}' AND after_state->>'outcome' = 'INVALID_CREDENTIALS'"));
    }

    [Fact]
    public async Task ThePasswordIsStoredOnlyAsAnArgon2idHash()
    {
        var stored = await fixture.ScalarAsync<string>($"SELECT password_hash FROM rcs.user_credential WHERE user_id = '{pilotId}'");

        Assert.StartsWith("$argon2id$v=19$", stored, StringComparison.Ordinal);
        Assert.DoesNotContain(Password, stored, StringComparison.Ordinal);
        Assert.Equal("ARGON2ID", await fixture.ScalarAsync<string>($"SELECT algorithm FROM rcs.user_credential WHERE user_id = '{pilotId}'"));

        // Nothing anywhere in the database holds the password in readable form.
        Assert.Equal(0L, await fixture.ScalarAsync<long>(
            $"SELECT count(*) FROM rcs.audit_event WHERE after_state::text LIKE '%{Password}%' OR before_state::text LIKE '%{Password}%'"));
    }

    [Fact]
    public async Task ConsecutiveFailuresLockTheAccountAndTheLockIsReleasable()
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            await fixture.Authentication.SignInAsync("pilot.user", "yanlış parol budur", "10.0.0.5");
        }

        var locked = await fixture.Authentication.SignInAsync("pilot.user", Password, "10.0.0.5");
        Assert.Equal(SignInOutcome.LockedOut, locked.Outcome);
        Assert.NotNull(locked.LockedUntil);

        // SECURITY.md §6.4: somebody must be able to unstick a colleague, and it is audited.
        Assert.True((await fixture.Administration.ReleaseLockAsync(Admin, pilotId)).Succeeded);
        Assert.Equal(SignInOutcome.Succeeded, (await fixture.Authentication.SignInAsync("pilot.user", Password, "10.0.0.5")).Outcome);
    }

    [Fact]
    public async Task ASuspendedUserCannotSignInAndTheirLiveSessionsEndAtOnce()
    {
        var session = await SignInAsync();
        Assert.NotNull(await fixture.Authentication.ValidateSessionAsync(session, "10.0.0.5"));

        var rowVersion = await fixture.ScalarAsync<int>($"SELECT row_version FROM rcs.app_user WHERE id = '{pilotId}'");
        Assert.True((await fixture.Administration.SetStatusAsync(Admin, pilotId, UserStatus.Suspended, "pilot ended", rowVersion)).Succeeded);

        // Invariant 5: revocation is immediate, not at expiry.
        Assert.Null(await fixture.Authentication.ValidateSessionAsync(session, "10.0.0.5"));
        Assert.Equal(SignInOutcome.AccountNotActive, (await fixture.Authentication.SignInAsync("pilot.user", Password, "10.0.0.5")).Outcome);
    }

    [Fact]
    public async Task SigningOutEndsTheSessionOnTheServer()
    {
        var session = await SignInAsync();
        await fixture.Authentication.SignOutAsync(session, "10.0.0.5");

        Assert.Null(await fixture.Authentication.ValidateSessionAsync(session, "10.0.0.5"));
        Assert.Equal("SIGNED_OUT", await fixture.ScalarAsync<string>($"SELECT revocation_reason FROM rcs.user_session WHERE id = '{session}'"));
        Assert.Equal(1L, await fixture.ScalarAsync<long>($"SELECT count(*) FROM rcs.audit_event WHERE action_code = 'LOGOUT' AND actor_user_id = '{pilotId}'"));
    }

    [Fact]
    public async Task ChangingThePasswordClearsTheMustChangeFlagAndEndsOtherSessions()
    {
        var elsewhere = await SignInAsync();
        var here = await SignInAsync();
        const string replacement = "yeni uzun və yadda qalan ifadə";

        var result = await fixture.Authentication.ChangePasswordAsync(pilotId, here, Password, replacement, "10.0.0.5");
        Assert.True(result.Succeeded, result.Error?.Code);

        Assert.Null(await fixture.Authentication.ValidateSessionAsync(elsewhere, "10.0.0.5"));
        Assert.NotNull(await fixture.Authentication.ValidateSessionAsync(here, "10.0.0.5"));
        Assert.Equal(SignInOutcome.InvalidCredentials, (await fixture.Authentication.SignInAsync("pilot.user", Password, "10.0.0.5")).Outcome);

        var again = await fixture.Authentication.SignInAsync("pilot.user", replacement, "10.0.0.5");
        Assert.Equal(SignInOutcome.Succeeded, again.Outcome);
        Assert.False(again.MustChangePassword);
    }

    [Fact]
    public async Task AWeakOrUnchangedNewPasswordIsRefused()
    {
        var session = await SignInAsync();

        Assert.Equal("auth.password_too_short", (await fixture.Authentication.ChangePasswordAsync(pilotId, session, Password, "qısa", "10.0.0.5")).Error!.Code);
        Assert.Equal("auth.password_unchanged", (await fixture.Authentication.ChangePasswordAsync(pilotId, session, Password, Password, "10.0.0.5")).Error!.Code);
        Assert.Equal("auth.current_password_wrong", (await fixture.Authentication.ChangePasswordAsync(pilotId, session, "səhv cari parol", "başqa uzun ifadə var", "10.0.0.5")).Error!.Code);
    }

    [Fact]
    public async Task AnAdministrativeResetIssuesATemporaryCredentialAndEndsEverySession()
    {
        var session = await SignInAsync();

        var reset = await fixture.Administration.ResetPasswordAsync(Admin, pilotId);
        Assert.True(reset.Succeeded);
        var temporary = reset.Value!;

        Assert.Null(await fixture.Authentication.ValidateSessionAsync(session, "10.0.0.5"));
        var signIn = await fixture.Authentication.SignInAsync("pilot.user", temporary, "10.0.0.5");
        Assert.Equal(SignInOutcome.Succeeded, signIn.Outcome);
        Assert.True(signIn.MustChangePassword);

        // The value itself never reaches the audit trail (SECURITY.md §6.2).
        Assert.Equal(0L, await fixture.ScalarAsync<long>($"SELECT count(*) FROM rcs.audit_event WHERE after_state::text LIKE '%{temporary}%'"));
    }

    [Fact]
    public async Task OnlyAHeadGrantsRolesAndOnlyATechAdminCreatesAccounts()
    {
        // The bootstrap exception: with no Head yet, the TechAdmin performs the first Head grant (PERMISSIONS.md §25.2).
        Assert.True((await fixture.Bootstrap.GrantRoleAsync("pilot.user", BusinessRole.Head, "tech.admin")).Succeeded);

        // With a Head in place, the console has no privilege the rules do not give it.
        var second = await fixture.Bootstrap.GrantRoleAsync("pilot.user", BusinessRole.Chief, "tech.admin");
        Assert.False(second.Succeeded);
        Assert.Equal("auth.head_grants_roles", second.Error!.Code);

        // The Head grants the rest; the TechAdmin cannot.
        var pilot = new ActorContext(pilotId, "integration-test");
        Assert.True((await fixture.Administration.GrantRoleAsync(pilot, pilotId, BusinessRole.Chief)).Succeeded);
        Assert.True((await fixture.Administration.GrantRoleAsync(pilot, pilotId, BusinessRole.Worker)).Succeeded);
        Assert.Equal("auth.head_grants_roles", (await fixture.Administration.GrantRoleAsync(Admin, pilotId, BusinessRole.Worker)).Error!.Code);

        // And the Head cannot create accounts, however senior they are.
        Assert.Equal("auth.not_permitted", (await fixture.Administration.CreateUserAsync(pilot, new CreateUserCommand("yeni.user", "Yeni", "Yeni", null, null))).Error!.Code);

        // Every grant in the database was made by the Head or the bootstrap administrator, never by anyone else.
        Assert.Equal(0L, await fixture.ScalarAsync<long>(
            "SELECT count(*) FROM rcs.user_role WHERE granted_by_user_id IS NULL"));
    }

    [Fact]
    public async Task RevokingARoleTakesEffectImmediately()
    {
        Assert.True((await fixture.Bootstrap.GrantRoleAsync("pilot.user", BusinessRole.Head, "tech.admin")).Succeeded);
        var pilot = new ActorContext(pilotId, "integration-test");
        Assert.True((await fixture.Administration.GrantRoleAsync(pilot, pilotId, BusinessRole.Worker)).Succeeded);

        var before = await fixture.Users.GetAsync(pilotId);
        Assert.Contains(BusinessRole.Worker, before!.Roles);

        Assert.True((await fixture.Administration.RevokeRoleAsync(pilot, pilotId, BusinessRole.Worker, "pilot over")).Succeeded);

        // Roles are read at action time (PERMISSIONS.md §27.3): the next read no longer has it.
        var after = await fixture.Users.GetAsync(pilotId);
        Assert.DoesNotContain(BusinessRole.Worker, after!.Roles);
        Assert.Contains(BusinessRole.Head, after.Roles);

        // The grant row survives with its window closed, so audit can still answer what they held when they acted.
        Assert.Equal(1L, await fixture.ScalarAsync<long>(
            $"SELECT count(*) FROM rcs.user_role WHERE user_id = '{pilotId}' AND valid_until IS NOT NULL AND revoked_by_user_id = '{pilotId}'"));
    }

    [Fact]
    public async Task ANewAccountHoldsNoRolesAndCanDoNothing()
    {
        var created = await fixture.Administration.CreateUserAsync(Admin, new CreateUserCommand("yeni.emekdas", "Yeni Əməkdaş", "Yeni Əməkdaş", "Mütəxəssis", null));
        Assert.True(created.Succeeded, created.Error?.Code);

        var profile = await fixture.Users.GetAsync(created.Value);
        Assert.Empty(profile!.Roles);

        // No credential either: a TechAdmin sets one deliberately, and it is temporary.
        Assert.Equal(0L, await fixture.ScalarAsync<long>($"SELECT count(*) FROM rcs.user_credential WHERE user_id = '{created.Value}'"));
        Assert.Equal(SignInOutcome.InvalidCredentials, (await fixture.Authentication.SignInAsync("yeni.emekdas", Password, "10.0.0.5")).Outcome);
    }
}
