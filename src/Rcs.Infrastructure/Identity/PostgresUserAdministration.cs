using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using Rcs.Application.Common;
using Rcs.Application.Identifiers;
using Rcs.Application.Identity;
using Rcs.Application.Persistence;
using Rcs.Domain.Authorization;
using Rcs.Domain.Vocabulary;
using Rcs.Infrastructure.Audit;
using Rcs.Infrastructure.Configuration;
using Rcs.Infrastructure.Persistence;

namespace Rcs.Infrastructure.Identity;

/// <summary>
/// Account and role administration (PERMISSIONS.md §25). The split is enforced here, not only in the UI: account
/// existence, credentials and status are TechAdmin's; granting and revoking roles is the Head's alone, so every
/// <c>user_role.granted_by_user_id</c> in the database is a Head — which is what stops a technical administrator
/// from minting business authority for themselves or anyone else.
/// </summary>
internal sealed class PostgresUserAdministration(
    IUnitOfWorkFactory unitOfWorkFactory,
    IPasswordHasher hasher,
    AuditWriter audit,
    IIdGenerator ids,
    TimeProvider timeProvider,
    IOptions<LocalAuthenticationOptions> options) : IUserAdministration
{
    private readonly LocalAuthenticationOptions settings = options.Value;

    public async Task<CommandResult<IReadOnlyList<UserAccountView>>> ListAsync(ActorContext actor, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        await using var unitOfWork = (PostgresUnitOfWork)await unitOfWorkFactory.BeginAsync(cancellationToken);
        var now = timeProvider.GetUtcNow();
        if (await AuthorizeAsync(unitOfWork, actor, AdministrationAction.ViewUsers, now, cancellationToken) is null)
        {
            return CommandResult<IReadOnlyList<UserAccountView>>.Failure(CommandErrorKind.Forbidden, "auth.not_permitted");
        }

        var rows = await unitOfWork.Command("""
                SELECT u.id, u.username, u.full_name, u.display_name, u.job_title, u.status, u.row_version,
                       c.user_id IS NOT NULL AS has_credential, COALESCE(c.must_change, false) AS must_change,
                       c.locked_until, c.last_success_at,
                       COALESCE(array_agg(r.code) FILTER (WHERE r.code IS NOT NULL), ARRAY[]::text[]) AS roles
                FROM rcs.app_user AS u
                LEFT JOIN rcs.user_credential AS c ON c.user_id = u.id
                LEFT JOIN rcs.user_role AS ur ON ur.user_id = u.id AND ur.valid_from <= @now AND (ur.valid_until IS NULL OR ur.valid_until > @now)
                LEFT JOIN rcs.role AS r ON r.id = ur.role_id AND r.is_active
                GROUP BY u.id, c.user_id, c.must_change, c.locked_until, c.last_success_at
                ORDER BY u.display_name
                """)
            .With("now", now)
            .ListAsync(reader => new UserAccountView(
                reader.Uuid("id"),
                reader.Text("username"),
                reader.Text("full_name"),
                reader.Text("display_name"),
                reader.TextOrNull("job_title"),
                VocabularyCodes.FromCode<UserStatus>(reader.Text("status")),
                reader.TextArray("roles").Select(VocabularyCodes.FromCode<BusinessRole>).OrderBy(role => role).ToArray(),
                reader.Bool("has_credential"),
                reader.Bool("must_change"),
                reader.InstantOrNull("locked_until"),
                reader.InstantOrNull("last_success_at"),
                reader.Int("row_version")), cancellationToken);
        return CommandResult<IReadOnlyList<UserAccountView>>.Success(rows);
    }

    public async Task<CommandResult<Guid>> CreateUserAsync(ActorContext actor, CreateUserCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(command);
        await using var unitOfWork = (PostgresUnitOfWork)await unitOfWorkFactory.BeginAsync(cancellationToken);
        var now = timeProvider.GetUtcNow();
        var administrator = await AuthorizeAsync(unitOfWork, actor, AdministrationAction.CreateUser, now, cancellationToken);
        if (administrator is null)
        {
            return CommandResult<Guid>.Failure(CommandErrorKind.Forbidden, "auth.not_permitted");
        }

        var username = (command.Username ?? string.Empty).Trim().ToLowerInvariant();
        if (username.Length is < 3 or > 64 || !username.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-') || !char.IsAsciiLetterOrDigit(username[0]))
        {
            return CommandResult<Guid>.Failure(CommandErrorKind.Validation, "auth.username_invalid", "username");
        }

        if (string.IsNullOrWhiteSpace(command.FullName) || string.IsNullOrWhiteSpace(command.DisplayName))
        {
            return CommandResult<Guid>.Failure(CommandErrorKind.Validation, "validation.required", "fullName");
        }

        if (await AuthSql.FindUserIdAsync(unitOfWork, username, cancellationToken) is not null)
        {
            return CommandResult<Guid>.Failure(CommandErrorKind.Conflict, "auth.username_taken", "username");
        }

        // A new account holds no role and can do nothing until a Head grants one (PERMISSIONS.md §25.2).
        var userId = ids.NewId();
        await unitOfWork.Command("""
                INSERT INTO rcs.app_user (id, username, employee_number, full_name, display_name, job_title, status, auth_source, created_by_user_id)
                VALUES (@id, @username, @employee_number, @full_name, @display_name, @job_title, 'ACTIVE', 'LOCAL', @by)
                """)
            .With("id", userId)
            .With("username", username)
            .With("employee_number", string.IsNullOrWhiteSpace(command.EmployeeNumber) ? null : command.EmployeeNumber.Trim())
            .With("full_name", command.FullName.Trim())
            .With("display_name", command.DisplayName.Trim())
            .With("job_title", string.IsNullOrWhiteSpace(command.JobTitle) ? null : command.JobTitle.Trim())
            .With("by", administrator.UserId)
            .ExecuteAsync(cancellationToken);

        await audit.WriteAsync(unitOfWork, administrator, actor.ClientHost, ids.NewId(), new AuditEntry(
            AuditActionCodes.Create, AuditEntityTypes.User, userId, 1, null,
            After: new { username, display_name = command.DisplayName.Trim(), status = UserStatus.Active.ToCode(), auth_source = "LOCAL" }), cancellationToken);
        await unitOfWork.CommitAsync(cancellationToken);
        return CommandResult<Guid>.Success(userId);
    }

    public async Task<CommandResult<string>> ResetPasswordAsync(ActorContext actor, Guid userId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        await using var unitOfWork = (PostgresUnitOfWork)await unitOfWorkFactory.BeginAsync(cancellationToken);
        var now = timeProvider.GetUtcNow();
        var administrator = await AuthorizeAsync(unitOfWork, actor, AdministrationAction.SetCredential, now, cancellationToken);
        if (administrator is null)
        {
            return CommandResult<string>.Failure(CommandErrorKind.Forbidden, "auth.not_permitted");
        }

        var target = await ActorStore.LoadAsync(unitOfWork, userId, now, cancellationToken);
        if (target is null)
        {
            return CommandResult<string>.Failure(CommandErrorKind.NotFound, "notfound.user");
        }

        // The administrator never chooses the value and never learns the old one (SECURITY.md §6.7 step 2).
        var temporary = GenerateTemporaryPassword();
        await AuthSql.UpsertCredentialAsync(unitOfWork, userId, hasher.Algorithm, hasher.Parameters, hasher.Hash(temporary),
            mustChange: true, isTemporary: true, expiresAt: now + TimeSpan.FromHours(Math.Max(1, settings.TemporaryCredentialHours)),
            setBy: administrator.UserId, now, cancellationToken);
        var revoked = await AuthSql.RevokeUserSessionsAsync(unitOfWork, userId, SessionRevocation.Administrative, now, null, cancellationToken);

        // The value itself is never audited, never logged and never stored in readable form (§6.2).
        await audit.WriteAsync(unitOfWork, administrator, actor.ClientHost, ids.NewId(), new AuditEntry(
            AuditActionCodes.Update, AuditEntityTypes.User, userId, null, null,
            After: new { change = "CREDENTIAL_RESET", temporary = true, expires_in_hours = settings.TemporaryCredentialHours, sessions_revoked = revoked }), cancellationToken);
        await unitOfWork.CommitAsync(cancellationToken);
        return CommandResult<string>.Success(temporary);
    }

    public async Task<CommandResult<Guid>> SetStatusAsync(ActorContext actor, Guid userId, UserStatus status, string? reasonNote, int rowVersion, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        await using var unitOfWork = (PostgresUnitOfWork)await unitOfWorkFactory.BeginAsync(cancellationToken);
        var now = timeProvider.GetUtcNow();
        var administrator = await AuthorizeAsync(unitOfWork, actor, AdministrationAction.SetUserStatus, now, cancellationToken);
        if (administrator is null)
        {
            return CommandResult<Guid>.Failure(CommandErrorKind.Forbidden, "auth.not_permitted");
        }

        var target = await ActorStore.LoadAsync(unitOfWork, userId, now, cancellationToken);
        if (target is null)
        {
            return CommandResult<Guid>.Failure(CommandErrorKind.NotFound, "notfound.user");
        }

        if (userId == administrator.UserId && status != UserStatus.Active)
        {
            return CommandResult<Guid>.Failure(CommandErrorKind.RuleViolation, "auth.cannot_disable_self");
        }

        // §25.4: the guard delays only the final DEACTIVATED step, never suspension.
        if (status == UserStatus.Deactivated && await AuthSql.HasActiveResponsibilitiesAsync(unitOfWork, userId, now, cancellationToken))
        {
            return CommandResult<Guid>.Failure(CommandErrorKind.RuleViolation, "auth.responsibilities_remain");
        }

        var updated = await unitOfWork.Command("""
                UPDATE rcs.app_user
                SET status = @status,
                    deactivated_at = CASE WHEN @status = 'DEACTIVATED' THEN @now ELSE NULL END,
                    deactivated_by_user_id = CASE WHEN @status = 'DEACTIVATED' THEN @by ELSE NULL END,
                    deactivation_reason_note = CASE WHEN @status = 'ACTIVE' THEN NULL ELSE @reason END,
                    updated_at = @now, updated_by_user_id = @by, row_version = row_version + 1
                WHERE id = @id AND row_version = @row_version
                """)
            .With("id", userId)
            .With("status", status.ToCode())
            .With("reason", reasonNote)
            .With("by", administrator.UserId)
            .With("now", now)
            .With("row_version", rowVersion)
            .ExecuteAsync(cancellationToken);
        if (updated != 1)
        {
            return CommandResult<Guid>.Failure(CommandErrorKind.Conflict, "concurrency.changed");
        }

        // Access removal is immediate: sign-in is blocked and every live session ends now (§25.3).
        var revoked = status == UserStatus.Active ? 0 : await AuthSql.RevokeUserSessionsAsync(unitOfWork, userId, SessionRevocation.AccountSuspended, now, null, cancellationToken);
        await audit.WriteAsync(unitOfWork, administrator, actor.ClientHost, ids.NewId(), new AuditEntry(
            AuditActionCodes.StateChange, AuditEntityTypes.User, userId, rowVersion + 1, null,
            Before: new { status = target.Status.ToCode() },
            After: new { status = status.ToCode(), sessions_revoked = revoked },
            ReasonNote: reasonNote), cancellationToken);
        await unitOfWork.CommitAsync(cancellationToken);
        return CommandResult<Guid>.Success(userId);
    }

    public async Task<CommandResult<Guid>> GrantRoleAsync(ActorContext actor, Guid userId, BusinessRole role, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        await using var unitOfWork = (PostgresUnitOfWork)await unitOfWorkFactory.BeginAsync(cancellationToken);
        var now = timeProvider.GetUtcNow();
        var head = await AuthorizeAsync(unitOfWork, actor, AdministrationAction.GrantRole, now, cancellationToken);
        if (head is null)
        {
            return CommandResult<Guid>.Failure(CommandErrorKind.Forbidden, "auth.head_grants_roles");
        }

        var target = await ActorStore.LoadAsync(unitOfWork, userId, now, cancellationToken);
        if (target is null)
        {
            return CommandResult<Guid>.Failure(CommandErrorKind.NotFound, "notfound.user");
        }

        if (target.Roles.Contains(role))
        {
            return CommandResult<Guid>.Failure(CommandErrorKind.Conflict, "auth.role_already_held");
        }

        var grantId = ids.NewId();
        await unitOfWork.Command("""
                INSERT INTO rcs.user_role (id, user_id, role_id, valid_from, granted_by_user_id)
                VALUES (@id, @user, @role, @now, @by)
                """)
            .With("id", grantId)
            .With("user", userId)
            .WithRole("role", role)
            .With("now", now)
            .With("by", head.UserId)
            .ExecuteAsync(cancellationToken);

        await audit.WriteAsync(unitOfWork, head, actor.ClientHost, ids.NewId(), new AuditEntry(
            AuditActionCodes.Assign, AuditEntityTypes.UserRole, grantId, 1, null,
            After: new { user_id = userId, role = role.ToCode(), granted_by = head.UserId }), cancellationToken);
        await unitOfWork.CommitAsync(cancellationToken);
        return CommandResult<Guid>.Success(grantId);
    }

    public async Task<CommandResult<Guid>> RevokeRoleAsync(ActorContext actor, Guid userId, BusinessRole role, string reasonNote, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        await using var unitOfWork = (PostgresUnitOfWork)await unitOfWorkFactory.BeginAsync(cancellationToken);
        var now = timeProvider.GetUtcNow();
        var head = await AuthorizeAsync(unitOfWork, actor, AdministrationAction.GrantRole, now, cancellationToken);
        if (head is null)
        {
            return CommandResult<Guid>.Failure(CommandErrorKind.Forbidden, "auth.head_grants_roles");
        }

        if (string.IsNullOrWhiteSpace(reasonNote))
        {
            return CommandResult<Guid>.Failure(CommandErrorKind.Validation, "validation.required", "reason");
        }

        // The grant window closes; the row stays, so audit can still answer "what role did they hold when they acted?".
        var closed = await unitOfWork.Command("""
                UPDATE rcs.user_role
                SET valid_until = @now, revoked_by_user_id = @by, revocation_reason_note = @reason
                WHERE user_id = @user AND role_id = @role AND valid_from <= @now AND (valid_until IS NULL OR valid_until > @now)
                RETURNING id
                """)
            .With("user", userId)
            .WithRole("role", role)
            .With("now", now)
            .With("by", head.UserId)
            .With("reason", reasonNote.Trim())
            .ListAsync(reader => reader.Uuid("id"), cancellationToken);
        if (closed.Count == 0)
        {
            return CommandResult<Guid>.Failure(CommandErrorKind.NotFound, "auth.role_not_held");
        }

        await audit.WriteAsync(unitOfWork, head, actor.ClientHost, ids.NewId(), new AuditEntry(
            AuditActionCodes.Assign, AuditEntityTypes.UserRole, closed[0], null, null,
            Before: new { user_id = userId, role = role.ToCode(), held = true },
            After: new { user_id = userId, role = role.ToCode(), held = false, revoked_by = head.UserId },
            ReasonNote: reasonNote.Trim()), cancellationToken);
        await unitOfWork.CommitAsync(cancellationToken);
        return CommandResult<Guid>.Success(closed[0]);
    }

    public async Task<CommandResult<Guid>> ReleaseLockAsync(ActorContext actor, Guid userId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        await using var unitOfWork = (PostgresUnitOfWork)await unitOfWorkFactory.BeginAsync(cancellationToken);
        var now = timeProvider.GetUtcNow();
        var administrator = await AuthorizeAsync(unitOfWork, actor, AdministrationAction.SetCredential, now, cancellationToken);
        if (administrator is null)
        {
            return CommandResult<Guid>.Failure(CommandErrorKind.Forbidden, "auth.not_permitted");
        }

        await AuthSql.ReleaseLockAsync(unitOfWork, userId, now, cancellationToken);
        await audit.WriteAsync(unitOfWork, administrator, actor.ClientHost, ids.NewId(), new AuditEntry(
            AuditActionCodes.Update, AuditEntityTypes.User, userId, null, null,
            After: new { change = "SIGN_IN_LOCK_RELEASED" }), cancellationToken);
        await unitOfWork.CommitAsync(cancellationToken);
        return CommandResult<Guid>.Success(userId);
    }

    /// <summary>
    /// A readable, dictation-friendly temporary password: five random groups from an unambiguous alphabet. It is
    /// handed over in person, is single-use in practice (the user must replace it) and expires quickly.
    /// </summary>
    public static string GenerateTemporaryPassword()
    {
        const string alphabet = "abcdefghijkmnpqrstuvwxyz23456789";
        Span<char> value = stackalloc char[29];
        var index = 0;
        for (var group = 0; group < 5; group++)
        {
            if (group > 0)
            {
                value[index++] = '-';
            }

            for (var character = 0; character < 5; character++)
            {
                value[index++] = alphabet[RandomNumberGenerator.GetInt32(alphabet.Length)];
            }
        }

        return new string(value);
    }

    private async Task<ActorProfile?> AuthorizeAsync(PostgresUnitOfWork unitOfWork, ActorContext actor, AdministrationAction action, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var profile = await ActorStore.LoadAsync(unitOfWork, actor.UserId, now, cancellationToken);
        return profile is not null && AdministrationPolicy.Decide(profile.Authority(), action).IsAllowed ? profile : null;
    }
}
