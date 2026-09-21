using Rcs.Application.Cases;
using Rcs.Application.Common;
using Rcs.Domain.Vocabulary;

namespace Rcs.Application.Identity;

/// <summary>
/// Why a sign-in attempt ended the way it did. The UI shows one generic message for every failure — the distinction
/// exists for the audit trail and the server log, never for the response (SECURITY.md §6.4).
/// </summary>
public enum SignInOutcome
{
    Succeeded = 1,
    InvalidCredentials,

    /// <summary>Suspended or deactivated: cannot authenticate at all (SECURITY.md §6.5).</summary>
    AccountNotActive,

    /// <summary>Too many consecutive failures; the lock releases itself after a configured interval.</summary>
    LockedOut,

    /// <summary>A temporary administrative credential that was not used in time (SECURITY.md §6.7 step 3).</summary>
    CredentialExpired,
}

/// <param name="MustChangePassword">A first sign-in or an administrative reset: nothing else may happen first (§6.3).</param>
public sealed record SignInResult(
    SignInOutcome Outcome,
    ActorProfile? Profile,
    Guid? SessionId,
    bool MustChangePassword,
    DateTimeOffset? LockedUntil)
{
    public bool Succeeded => Outcome == SignInOutcome.Succeeded;
}

/// <summary>A live session resolved from the request's cookie, re-read from the database on every request.</summary>
public sealed record AuthenticatedSession(Guid SessionId, ActorProfile Profile, bool MustChangePassword);

/// <summary>
/// Local username/password authentication with server-side, revocable sessions (ADR-033). There is no "sign in as
/// another user" anywhere in this interface, and there never will be (SECURITY.md §6.1, invariant 18).
/// </summary>
public interface ILocalAuthenticationService
{
    Task<SignInResult> SignInAsync(string username, string password, string? clientHost, CancellationToken cancellationToken = default);

    /// <summary>
    /// The session behind a cookie, or null when it is unknown, revoked, expired, idle too long, or belongs to a user
    /// who is no longer ACTIVE. Called on every authenticated request, which is what makes revocation immediate.
    /// </summary>
    Task<AuthenticatedSession?> ValidateSessionAsync(Guid sessionId, string? clientHost, CancellationToken cancellationToken = default);

    Task SignOutAsync(Guid sessionId, string? clientHost, CancellationToken cancellationToken = default);

    /// <summary>
    /// The user replaces their own password. Every other session of that account is revoked, and the current one is
    /// kept, so a password change does not sign the person out of the screen they are on.
    /// </summary>
    Task<CommandResult<Guid>> ChangePasswordAsync(Guid userId, Guid currentSessionId, string currentPassword, string newPassword, string? clientHost, CancellationToken cancellationToken = default);
}

/// <summary>One account as the administration screens show it. No hash, no parameters, nothing credential-shaped.</summary>
public sealed record UserAccountView(
    Guid Id,
    string Username,
    string FullName,
    string DisplayName,
    string? JobTitle,
    UserStatus Status,
    IReadOnlyList<BusinessRole> Roles,
    bool HasCredential,
    bool MustChangePassword,
    DateTimeOffset? LockedUntil,
    DateTimeOffset? LastSignInAt,
    int RowVersion);

public sealed record CreateUserCommand(string Username, string FullName, string DisplayName, string? JobTitle, string? EmployeeNumber);

/// <summary>
/// Account and role administration, split exactly as PERMISSIONS.md §25.2 splits it: TechAdmin owns account
/// existence and credentials, the Head alone grants and revokes roles. Every method re-checks that split itself.
/// </summary>
public interface IUserAdministration
{
    Task<CommandResult<IReadOnlyList<UserAccountView>>> ListAsync(ActorContext actor, CancellationToken cancellationToken = default);

    Task<CommandResult<Guid>> CreateUserAsync(ActorContext actor, CreateUserCommand command, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sets a temporary credential the user must replace at next sign-in, and revokes their sessions (§6.7 steps 2–5).
    /// The generated value is returned once, to be handed over in person; it is never stored in readable form or audited.
    /// </summary>
    Task<CommandResult<string>> ResetPasswordAsync(ActorContext actor, Guid userId, CancellationToken cancellationToken = default);

    /// <summary>Suspension revokes every session at once; deactivation is refused while ACTIVE responsibilities remain (§25.4).</summary>
    Task<CommandResult<Guid>> SetStatusAsync(ActorContext actor, Guid userId, UserStatus status, string? reasonNote, int rowVersion, CancellationToken cancellationToken = default);

    Task<CommandResult<Guid>> GrantRoleAsync(ActorContext actor, Guid userId, BusinessRole role, CancellationToken cancellationToken = default);

    Task<CommandResult<Guid>> RevokeRoleAsync(ActorContext actor, Guid userId, BusinessRole role, string reasonNote, CancellationToken cancellationToken = default);

    /// <summary>Releases a sign-in lock immediately, audited — somebody must be able to unstick a colleague (§6.4).</summary>
    Task<CommandResult<Guid>> ReleaseLockAsync(ActorContext actor, Guid userId, CancellationToken cancellationToken = default);
}

/// <summary>How a password is hashed. The only implementation is Argon2id (ADR-033, SECURITY.md §6.2).</summary>
public interface IPasswordHasher
{
    string Algorithm { get; }

    /// <summary>The cost this server is configured for, recorded with every hash so it can be reviewed later.</summary>
    string Parameters { get; }

    string Hash(string password);

    /// <summary>
    /// Constant-time verification. <c>NeedsRehash</c> is true when the stored hash used weaker parameters than this
    /// server is configured for, so the password is re-hashed transparently at that sign-in (§6.2).
    /// </summary>
    (bool Verified, bool NeedsRehash) Verify(string encodedHash, string password);
}
