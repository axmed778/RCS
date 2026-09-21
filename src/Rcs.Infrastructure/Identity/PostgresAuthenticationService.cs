using Microsoft.Extensions.Logging;
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

/// <summary>Why a session was ended, as stored on the session row.</summary>
internal static class SessionRevocation
{
    public const string SignedOut = "SIGNED_OUT";
    public const string PasswordChanged = "PASSWORD_CHANGED";
    public const string AccountSuspended = "ACCOUNT_SUSPENDED";
    public const string Administrative = "ADMINISTRATIVE";
}

/// <summary>
/// Local sign-in against <c>user_credential</c>, with server-side sessions (ADR-033; SECURITY.md §6, §8).
/// </summary>
/// <remarks>
/// Three properties are load-bearing and are why this is one place rather than several:
/// every failure returns the same generic outcome to the caller (§6.4); every attempt is throttled per account and
/// recorded; and a session is re-validated against the database on every request, so suspending a user or changing a
/// password ends access at once rather than at expiry (§6.5, invariant 5).
/// </remarks>
internal sealed class PostgresAuthenticationService(
    IUnitOfWorkFactory unitOfWorkFactory,
    IPasswordHasher hasher,
    AuditWriter audit,
    IIdGenerator ids,
    TimeProvider timeProvider,
    IOptions<LocalAuthenticationOptions> options,
    ILogger<PostgresAuthenticationService> logger) : ILocalAuthenticationService
{
    private readonly LocalAuthenticationOptions settings = options.Value;

    public async Task<SignInResult> SignInAsync(string username, string password, string? clientHost, CancellationToken cancellationToken = default)
    {
        var now = timeProvider.GetUtcNow();
        await using var unitOfWork = (PostgresUnitOfWork)await unitOfWorkFactory.BeginAsync(cancellationToken);

        var userId = string.IsNullOrWhiteSpace(username) || username.Length > 64 || password.Length > 1024 ? null : await AuthSql.FindUserIdAsync(unitOfWork, username.Trim().ToLowerInvariant(), cancellationToken);
        if (userId is null)
        {
            // An unknown username has no account row to audit against and no counter to raise; it is logged only.
            // The work of hashing is skipped, which is a timing signal we accept: the department's usernames are known.
            logger.LogWarning("Sign-in failed for an unknown username from {ClientHost}.", clientHost);
            return new SignInResult(SignInOutcome.InvalidCredentials, null, null, false, null);
        }

        var profile = await ActorStore.LoadAsync(unitOfWork, userId.Value, now, cancellationToken);
        var credential = await AuthSql.LoadCredentialAsync(unitOfWork, userId.Value, forUpdate: true, cancellationToken);
        if (profile is null || credential is null)
        {
            logger.LogWarning("Sign-in failed for {UserId}: no profile or no local credential.", userId);
            return new SignInResult(SignInOutcome.InvalidCredentials, null, null, false, null);
        }

        if (credential.LockedUntil is { } lockedUntil && lockedUntil > now)
        {
            await AuditAsync(unitOfWork, profile, clientHost, "LOCKED_OUT", now, cancellationToken);
            await unitOfWork.CommitAsync(cancellationToken);
            return new SignInResult(SignInOutcome.LockedOut, null, null, false, lockedUntil);
        }

        var (verified, needsRehash) = hasher.Verify(credential.PasswordHash, password);
        if (!verified)
        {
            await AuthSql.RecordFailureAsync(unitOfWork, userId.Value, settings.MaxFailedAttempts, TimeSpan.FromMinutes(Math.Max(1, settings.LockoutMinutes)), now, cancellationToken);
            await AuditAsync(unitOfWork, profile, clientHost, "INVALID_CREDENTIALS", now, cancellationToken);
            await unitOfWork.CommitAsync(cancellationToken);
            return new SignInResult(SignInOutcome.InvalidCredentials, null, null, false, null);
        }

        // Status is checked after verification so that a wrong password and a suspended account are indistinguishable
        // to someone guessing, while the audit trail still records exactly which one it was.
        if (profile.Status != UserStatus.Active)
        {
            await AuditAsync(unitOfWork, profile, clientHost, "ACCOUNT_" + profile.Status.ToCode(), now, cancellationToken);
            await unitOfWork.CommitAsync(cancellationToken);
            return new SignInResult(SignInOutcome.AccountNotActive, null, null, false, null);
        }

        if (credential is { IsTemporary: true, ExpiresAt: { } expiry } && expiry <= now)
        {
            await AuditAsync(unitOfWork, profile, clientHost, "CREDENTIAL_EXPIRED", now, cancellationToken);
            await unitOfWork.CommitAsync(cancellationToken);
            return new SignInResult(SignInOutcome.CredentialExpired, null, null, false, null);
        }

        if (needsRehash)
        {
            await AuthSql.RehashAsync(unitOfWork, userId.Value, hasher.Algorithm, hasher.Parameters, hasher.Hash(password), now, cancellationToken);
        }

        var sessionId = ids.NewId();
        await AuthSql.CreateSessionAsync(unitOfWork, sessionId, userId.Value, clientHost, now, TimeSpan.FromHours(Math.Max(1, settings.SessionLifetimeHours)), cancellationToken);
        await AuthSql.RecordSuccessAsync(unitOfWork, userId.Value, now, cancellationToken);
        await audit.WriteAsync(unitOfWork, profile, clientHost, ids.NewId(), new AuditEntry(
            AuditActionCodes.Login, AuditEntityTypes.User, profile.UserId, null, null,
            After: new { outcome = "SUCCEEDED", session_id = sessionId, must_change_password = credential.MustChange }), cancellationToken);
        await unitOfWork.CommitAsync(cancellationToken);
        return new SignInResult(SignInOutcome.Succeeded, profile, sessionId, credential.MustChange, null);
    }

    public async Task<AuthenticatedSession?> ValidateSessionAsync(Guid sessionId, string? clientHost, CancellationToken cancellationToken = default)
    {
        var now = timeProvider.GetUtcNow();
        await using var unitOfWork = (PostgresUnitOfWork)await unitOfWorkFactory.BeginAsync(cancellationToken);
        var session = await AuthSql.LoadSessionAsync(unitOfWork, sessionId, cancellationToken);
        if (session is null || session.RevokedAt is not null || session.ExpiresAt <= now)
        {
            return null;
        }

        if (session.LastSeenAt + TimeSpan.FromMinutes(Math.Max(1, settings.IdleTimeoutMinutes)) <= now)
        {
            await AuthSql.RevokeSessionAsync(unitOfWork, sessionId, SessionRevocation.SignedOut, now, cancellationToken);
            await unitOfWork.CommitAsync(cancellationToken);
            return null;
        }

        var profile = await ActorStore.LoadAsync(unitOfWork, session.UserId, now, cancellationToken);
        if (profile is null || profile.Status != UserStatus.Active)
        {
            // Suspension takes effect on the next request, not at expiry (SECURITY.md §6.5).
            await AuthSql.RevokeSessionAsync(unitOfWork, sessionId, SessionRevocation.AccountSuspended, now, cancellationToken);
            await unitOfWork.CommitAsync(cancellationToken);
            return null;
        }

        var credential = await AuthSql.LoadCredentialAsync(unitOfWork, session.UserId, forUpdate: false, cancellationToken);
        if (credential is null || (credential.IsTemporary && credential.ExpiresAt <= now))
        {
            return null;
        }

        await AuthSql.TouchSessionAsync(unitOfWork, sessionId, now, cancellationToken);
        await unitOfWork.CommitAsync(cancellationToken);
        return new AuthenticatedSession(sessionId, profile, credential?.MustChange ?? false);
    }

    public async Task SignOutAsync(Guid sessionId, string? clientHost, CancellationToken cancellationToken = default)
    {
        var now = timeProvider.GetUtcNow();
        await using var unitOfWork = (PostgresUnitOfWork)await unitOfWorkFactory.BeginAsync(cancellationToken);
        var session = await AuthSql.LoadSessionAsync(unitOfWork, sessionId, cancellationToken);
        if (session is null || session.RevokedAt is not null)
        {
            return;
        }

        await AuthSql.RevokeSessionAsync(unitOfWork, sessionId, SessionRevocation.SignedOut, now, cancellationToken);
        if (await ActorStore.LoadAsync(unitOfWork, session.UserId, now, cancellationToken) is { } profile)
        {
            await audit.WriteAsync(unitOfWork, profile, clientHost, ids.NewId(), new AuditEntry(
                AuditActionCodes.Logout, AuditEntityTypes.User, profile.UserId, null, null,
                After: new { session_id = sessionId }), cancellationToken);
        }

        await unitOfWork.CommitAsync(cancellationToken);
    }

    public async Task<CommandResult<Guid>> ChangePasswordAsync(
        Guid userId, Guid currentSessionId, string currentPassword, string newPassword, string? clientHost, CancellationToken cancellationToken = default)
    {
        var now = timeProvider.GetUtcNow();
        await using var unitOfWork = (PostgresUnitOfWork)await unitOfWorkFactory.BeginAsync(cancellationToken);
        var profile = await ActorStore.LoadAsync(unitOfWork, userId, now, cancellationToken);
        var credential = await AuthSql.LoadCredentialAsync(unitOfWork, userId, forUpdate: true, cancellationToken);
        if (profile is null || credential is null || profile.Status != UserStatus.Active)
        {
            return CommandResult<Guid>.Failure(CommandErrorKind.NotFound, "notfound.user");
        }

        var session = await AuthSql.LoadSessionAsync(unitOfWork, currentSessionId, cancellationToken);
        if (session is null || session.UserId != userId || session.RevokedAt is not null || session.ExpiresAt <= now
            || session.LastSeenAt.AddMinutes(settings.IdleTimeoutMinutes) <= now
            || credential.LockedUntil > now || (credential.IsTemporary && credential.ExpiresAt <= now))
        {
            return CommandResult<Guid>.Failure(CommandErrorKind.Forbidden, "auth.current_password_wrong");
        }

        if (!hasher.Verify(credential.PasswordHash, currentPassword).Verified)
        {
            await AuthSql.RecordFailureAsync(unitOfWork, userId, settings.MaxFailedAttempts, TimeSpan.FromMinutes(Math.Max(1, settings.LockoutMinutes)), now, cancellationToken);
            await unitOfWork.CommitAsync(cancellationToken);
            return CommandResult<Guid>.Failure(CommandErrorKind.Forbidden, "auth.current_password_wrong");
        }

        if (PasswordPolicy.Check(newPassword, profile.Username) is var rejection && rejection != PasswordRejection.None)
        {
            return CommandResult<Guid>.Failure(CommandErrorKind.Validation, PasswordCodes.Of(rejection), "newPassword");
        }

        if (hasher.Verify(credential.PasswordHash, newPassword).Verified)
        {
            return CommandResult<Guid>.Failure(CommandErrorKind.Validation, "auth.password_unchanged", "newPassword");
        }

        await AuthSql.UpsertCredentialAsync(unitOfWork, userId, hasher.Algorithm, hasher.Parameters, hasher.Hash(newPassword),
            mustChange: false, isTemporary: false, expiresAt: null, setBy: userId, now, cancellationToken);

        // Every other session of this account ends: a password change is how a user reacts to a suspected compromise.
        var revoked = await AuthSql.RevokeUserSessionsAsync(unitOfWork, userId, SessionRevocation.PasswordChanged, now, currentSessionId, cancellationToken);
        await audit.WriteAsync(unitOfWork, profile, clientHost, ids.NewId(), new AuditEntry(
            AuditActionCodes.Update, AuditEntityTypes.User, userId, null, null,
            After: new { change = "PASSWORD_CHANGED_BY_SELF", other_sessions_revoked = revoked }), cancellationToken);
        await unitOfWork.CommitAsync(cancellationToken);
        return CommandResult<Guid>.Success(userId);
    }

    private Task AuditAsync(PostgresUnitOfWork unitOfWork, ActorProfile profile, string? clientHost, string outcome, DateTimeOffset now, CancellationToken cancellationToken) =>
        audit.WriteAsync(unitOfWork, profile, clientHost, ids.NewId(), new AuditEntry(
            AuditActionCodes.Login, AuditEntityTypes.User, profile.UserId, null, null,
            After: new { outcome }, OccurredAt: now), cancellationToken);
}

/// <summary>Message codes for a refused password, resolved by the UI text catalogue.</summary>
public static class PasswordCodes
{
    public static string Of(PasswordRejection rejection) => rejection switch
    {
        PasswordRejection.TooShort => "auth.password_too_short",
        PasswordRejection.TooLong => "auth.password_too_long",
        PasswordRejection.TooCommon => "auth.password_too_common",
        PasswordRejection.ContainsUsername => "auth.password_contains_username",
        PasswordRejection.NotEnoughVariety => "auth.password_no_variety",
        _ => "auth.password_rejected",
    };
}
