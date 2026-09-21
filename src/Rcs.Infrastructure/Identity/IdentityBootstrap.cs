using Microsoft.Extensions.Options;
using Rcs.Application.Common;
using Rcs.Application.Identifiers;
using Rcs.Application.Identity;
using Rcs.Application.Persistence;
using Rcs.Domain.Identity;
using Rcs.Domain.Vocabulary;
using Rcs.Infrastructure.Audit;
using Rcs.Infrastructure.Configuration;
using Rcs.Infrastructure.Persistence;

namespace Rcs.Infrastructure.Identity;

/// <summary>What a bootstrap step did, for the console to print. Never contains a hash.</summary>
public sealed record BootstrapOutcome(Guid UserId, string Username, string? TemporaryPassword, string Summary);

/// <summary>
/// The installation-time identity steps of PERMISSIONS.md §25.2, and nothing else. They exist because a brand-new
/// database has no TechAdmin to create accounts and no Head to grant roles, so the very first ones must come from
/// somewhere: they come from the server console, performed by whoever installs the system, and they are written to the
/// audit trail like every other grant rather than being quietly special-cased.
/// </summary>
/// <remarks>
/// These methods are reachable only from the <c>Rcs.Web user …</c> commands, which require shell access to the server
/// and the runtime database credentials. They are never exposed over HTTP. Once a TechAdmin and a Head exist, all
/// further administration goes through the normal authorized paths (<see cref="IUserAdministration"/>).
/// </remarks>
public sealed class IdentityBootstrap(
    IUnitOfWorkFactory unitOfWorkFactory,
    IPasswordHasher hasher,
    AuditWriter audit,
    IIdGenerator ids,
    TimeProvider timeProvider,
    IOptions<LocalAuthenticationOptions> options)
{
    private readonly LocalAuthenticationOptions settings = options.Value;

    /// <summary>Creates (or finds) an account and gives it the TechAdmin role: the first administrator of a new database.</summary>
    public async Task<CommandResult<BootstrapOutcome>> CreateAdministratorAsync(
        string username, string fullName, string? displayName, string? password, CancellationToken cancellationToken = default)
    {
        await using var unitOfWork = (PostgresUnitOfWork)await unitOfWorkFactory.BeginAsync(cancellationToken);
        var now = timeProvider.GetUtcNow();
        await unitOfWork.Command("SELECT pg_advisory_xact_lock(724031600)").ExecuteAsync(cancellationToken);
        if (await unitOfWork.Command("SELECT EXISTS (SELECT 1 FROM rcs.user_role WHERE role_id = @role)")
            .WithRole("role", BusinessRole.TechAdmin).ScalarAsync<bool>(cancellationToken))
        {
            return CommandResult<BootstrapOutcome>.Failure(CommandErrorKind.Forbidden, "auth.bootstrap_already_completed");
        }

        var name = Normalize(username);
        var existing = await AuthSql.FindUserIdAsync(unitOfWork, name, cancellationToken);
        var userId = existing ?? ids.NewId();
        if (existing is null)
        {
            await InsertUserAsync(unitOfWork, userId, name, fullName, displayName, createdBy: userId, now, cancellationToken);
        }

        var credential = await SetCredentialAsync(unitOfWork, userId, name, password, setBy: userId, now, cancellationToken);
        if (!credential.Succeeded)
        {
            return credential.Cast<BootstrapOutcome>();
        }

        var granted = await GrantAsync(unitOfWork, userId, BusinessRole.TechAdmin, grantedBy: userId, now, cancellationToken);

        // Attributed to the new administrator themselves: the one unavoidable self-grant, and it is visible from day one.
        await audit.WriteAsync(unitOfWork, (await ActorStore.LoadAsync(unitOfWork, userId, now, cancellationToken))!, null, ids.NewId(), new AuditEntry(
            AuditActionCodes.Create, AuditEntityTypes.User, userId, 1, null,
            After: new { username = name, bootstrap = true, role_granted = granted ? BusinessRole.TechAdmin.ToCode() : null },
            ReasonNote: "Installation bootstrap: first technical administrator (PERMISSIONS.md 25.2)"), cancellationToken);
        await unitOfWork.CommitAsync(cancellationToken);
        return CommandResult<BootstrapOutcome>.Success(new BootstrapOutcome(userId, name, credential.Value,
            existing is null ? "administrator account created" : "existing account given a new credential"));
    }

    /// <summary>
    /// Creates an ordinary account from the console, attributed to an existing TechAdmin — the same act the
    /// administration screen performs, available before anyone can sign in to use that screen.
    /// </summary>
    public async Task<CommandResult<BootstrapOutcome>> CreateUserAsync(
        string username, string fullName, string? displayName, string? jobTitle, string? password, string byUsername, CancellationToken cancellationToken = default)
    {
        await using var unitOfWork = (PostgresUnitOfWork)await unitOfWorkFactory.BeginAsync(cancellationToken);
        var now = timeProvider.GetUtcNow();
        var administrator = await RequireRoleAsync(unitOfWork, byUsername, BusinessRole.TechAdmin, now, cancellationToken);
        if (administrator is null)
        {
            return CommandResult<BootstrapOutcome>.Failure(CommandErrorKind.Forbidden, "auth.bootstrap_actor_not_tech_admin");
        }

        var name = Normalize(username);
        if (await AuthSql.FindUserIdAsync(unitOfWork, name, cancellationToken) is not null)
        {
            return CommandResult<BootstrapOutcome>.Failure(CommandErrorKind.Conflict, "auth.username_taken");
        }

        var userId = ids.NewId();
        await InsertUserAsync(unitOfWork, userId, name, fullName, displayName, administrator.UserId, now, cancellationToken, jobTitle);
        var credential = await SetCredentialAsync(unitOfWork, userId, name, password, administrator.UserId, now, cancellationToken);
        if (!credential.Succeeded)
        {
            return credential.Cast<BootstrapOutcome>();
        }

        await audit.WriteAsync(unitOfWork, administrator, null, ids.NewId(), new AuditEntry(
            AuditActionCodes.Create, AuditEntityTypes.User, userId, 1, null,
            After: new { username = name, status = UserStatus.Active.ToCode(), auth_source = "LOCAL" },
            ReasonNote: "Created from the server console by a technical administrator"), cancellationToken);
        await unitOfWork.CommitAsync(cancellationToken);
        return CommandResult<BootstrapOutcome>.Success(new BootstrapOutcome(userId, name, credential.Value, "account created"));
    }

    /// <summary>
    /// Grants a role from the console. A Head grants normally; a TechAdmin may perform the <b>first</b> Head grant,
    /// which PERMISSIONS.md §25.2 names as the one documented installation exception — and only while no Head exists.
    /// </summary>
    public async Task<CommandResult<BootstrapOutcome>> GrantRoleAsync(string username, BusinessRole role, string byUsername, CancellationToken cancellationToken = default)
    {
        await using var unitOfWork = (PostgresUnitOfWork)await unitOfWorkFactory.BeginAsync(cancellationToken);
        var now = timeProvider.GetUtcNow();
        var granter = await ActorStore.LoadAsync(unitOfWork, await AuthSql.FindUserIdAsync(unitOfWork, Normalize(byUsername), cancellationToken) ?? Guid.Empty, now, cancellationToken);
        if (granter is null || granter.Status != UserStatus.Active)
        {
            return CommandResult<BootstrapOutcome>.Failure(CommandErrorKind.NotFound, "notfound.user");
        }

        await unitOfWork.Command("SELECT pg_advisory_xact_lock(724031600)").ExecuteAsync(cancellationToken);
        var headExists = await unitOfWork.Command("""
                SELECT EXISTS (
                    SELECT 1 FROM rcs.user_role AS ur JOIN rcs.role AS r ON r.id = ur.role_id
                    WHERE r.code = 'HEAD')
                """)
            .With("now", now)
            .ScalarAsync<bool>(cancellationToken);

        var isHead = granter.Roles.Contains(BusinessRole.Head);
        var isBootstrapException = !headExists && role == BusinessRole.Head && granter.Roles.Contains(BusinessRole.TechAdmin);
        if (!isHead && !isBootstrapException)
        {
            // Once a Head exists, the console has no privilege the rules do not give it: only a Head grants roles.
            return CommandResult<BootstrapOutcome>.Failure(CommandErrorKind.Forbidden, "auth.head_grants_roles");
        }

        var target = await ActorStore.LoadAsync(unitOfWork, await AuthSql.FindUserIdAsync(unitOfWork, Normalize(username), cancellationToken) ?? Guid.Empty, now, cancellationToken);
        if (target is null)
        {
            return CommandResult<BootstrapOutcome>.Failure(CommandErrorKind.NotFound, "notfound.user");
        }

        if (target.Roles.Contains(role))
        {
            return CommandResult<BootstrapOutcome>.Success(new BootstrapOutcome(target.UserId, target.Username, null, "role already held"));
        }

        var grantId = ids.NewId();
        await unitOfWork.Command("""
                INSERT INTO rcs.user_role (id, user_id, role_id, valid_from, granted_by_user_id)
                VALUES (@id, @user, @role, @now, @by)
                """)
            .With("id", grantId)
            .With("user", target.UserId)
            .WithRole("role", role)
            .With("now", now)
            .With("by", granter.UserId)
            .ExecuteAsync(cancellationToken);

        await audit.WriteAsync(unitOfWork, granter, null, ids.NewId(), new AuditEntry(
            AuditActionCodes.Assign, AuditEntityTypes.UserRole, grantId, 1, null,
            After: new { user_id = target.UserId, role = role.ToCode(), granted_by = granter.UserId, bootstrap_exception = isBootstrapException },
            ReasonNote: isBootstrapException
                ? "Installation bootstrap: first Head grant by the technical administrator (PERMISSIONS.md 25.2)"
                : "Granted from the server console by the Head"), cancellationToken);
        await unitOfWork.CommitAsync(cancellationToken);
        return CommandResult<BootstrapOutcome>.Success(new BootstrapOutcome(target.UserId, target.Username, null,
            $"granted {role.ToCode()}" + (isBootstrapException ? " (documented installation exception)" : string.Empty)));
    }

    /// <summary>Sets a credential from the console — the break-glass path when nobody can sign in to reset it (SECURITY.md §6.8).</summary>
    public async Task<CommandResult<BootstrapOutcome>> SetPasswordAsync(string username, string? password, string byUsername, CancellationToken cancellationToken = default)
    {
        await using var unitOfWork = (PostgresUnitOfWork)await unitOfWorkFactory.BeginAsync(cancellationToken);
        var now = timeProvider.GetUtcNow();
        var administrator = await RequireRoleAsync(unitOfWork, byUsername, BusinessRole.TechAdmin, now, cancellationToken);
        if (administrator is null)
        {
            return CommandResult<BootstrapOutcome>.Failure(CommandErrorKind.Forbidden, "auth.bootstrap_actor_not_tech_admin");
        }

        var userId = await AuthSql.FindUserIdAsync(unitOfWork, Normalize(username), cancellationToken);
        if (userId is null)
        {
            return CommandResult<BootstrapOutcome>.Failure(CommandErrorKind.NotFound, "notfound.user");
        }

        var credential = await SetCredentialAsync(unitOfWork, userId.Value, Normalize(username), password, administrator.UserId, now, cancellationToken);
        if (!credential.Succeeded)
        {
            return credential.Cast<BootstrapOutcome>();
        }

        var revoked = await AuthSql.RevokeUserSessionsAsync(unitOfWork, userId.Value, SessionRevocation.Administrative, now, null, cancellationToken);
        await audit.WriteAsync(unitOfWork, administrator, null, ids.NewId(), new AuditEntry(
            AuditActionCodes.Update, AuditEntityTypes.User, userId.Value, null, null,
            After: new { change = "CREDENTIAL_SET_FROM_CONSOLE", sessions_revoked = revoked }), cancellationToken);
        await unitOfWork.CommitAsync(cancellationToken);
        return CommandResult<BootstrapOutcome>.Success(new BootstrapOutcome(userId.Value, Normalize(username), credential.Value, "credential set"));
    }

    private static string Normalize(string username) => (username ?? string.Empty).Trim().ToLowerInvariant();

    private static Task InsertUserAsync(
        PostgresUnitOfWork unitOfWork, Guid userId, string username, string fullName, string? displayName, Guid createdBy, DateTimeOffset now, CancellationToken cancellationToken, string? jobTitle = null) =>
        unitOfWork.Command("""
                INSERT INTO rcs.app_user (id, username, full_name, display_name, job_title, status, auth_source, created_at, created_by_user_id)
                VALUES (@id, @username, @full_name, @display_name, @job_title, 'ACTIVE', 'LOCAL', @now, @by)
                """)
            .With("id", userId)
            .With("username", username)
            .With("full_name", fullName.Trim())
            .With("display_name", string.IsNullOrWhiteSpace(displayName) ? fullName.Trim() : displayName.Trim())
            .With("job_title", jobTitle)
            .With("now", now)
            .With("by", createdBy)
            .ExecuteAsync(cancellationToken);

    /// <summary>Writes the credential and returns the generated password when one was generated, so it can be handed over.</summary>
    private async Task<CommandResult<string?>> SetCredentialAsync(
        PostgresUnitOfWork unitOfWork, Guid userId, string username, string? password, Guid setBy, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var generated = string.IsNullOrEmpty(password);
        var value = generated ? PostgresUserAdministration.GenerateTemporaryPassword() : password!;
        if (!generated && PasswordPolicy.Check(value, username) is var rejection && rejection != PasswordRejection.None)
        {
            return CommandResult<string?>.Failure(CommandErrorKind.Validation, PasswordCodes.Of(rejection), "password");
        }

        await AuthSql.UpsertCredentialAsync(unitOfWork, userId, hasher.Algorithm, hasher.Parameters, hasher.Hash(value),
            mustChange: true,
            isTemporary: true,
            expiresAt: now + TimeSpan.FromHours(Math.Max(1, settings.TemporaryCredentialHours)),
            setBy, now, cancellationToken);
        return CommandResult<string?>.Success(generated ? value : null);
    }

    private static async Task<ActorProfile?> RequireRoleAsync(PostgresUnitOfWork unitOfWork, string username, BusinessRole role, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var userId = await AuthSql.FindUserIdAsync(unitOfWork, Normalize(username), cancellationToken);
        var profile = userId is null ? null : await ActorStore.LoadAsync(unitOfWork, userId.Value, now, cancellationToken);
        return profile is { Status: UserStatus.Active } && profile.Roles.Contains(role) ? profile : null;
    }

    private async Task<bool> GrantAsync(PostgresUnitOfWork unitOfWork, Guid userId, BusinessRole role, Guid grantedBy, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var held = await unitOfWork.Command("""
                SELECT EXISTS (
                    SELECT 1 FROM rcs.user_role WHERE user_id = @user AND role_id = @role
                      AND valid_from <= @now AND (valid_until IS NULL OR valid_until > @now))
                """)
            .With("user", userId)
            .WithRole("role", role)
            .With("now", now)
            .ScalarAsync<bool>(cancellationToken);
        if (held)
        {
            return false;
        }

        await unitOfWork.Command("""
                INSERT INTO rcs.user_role (id, user_id, role_id, valid_from, granted_by_user_id)
                VALUES (@id, @user, @role, @now, @by)
                """)
            .With("id", ids.NewId())
            .With("user", userId)
            .WithRole("role", role)
            .With("now", now)
            .With("by", grantedBy)
            .ExecuteAsync(cancellationToken);
        return true;
    }
}
