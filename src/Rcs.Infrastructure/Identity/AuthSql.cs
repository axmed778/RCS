using Npgsql;
using Rcs.Domain.Vocabulary;
using Rcs.Infrastructure.Persistence;

namespace Rcs.Infrastructure.Identity;

/// <summary>One user's current credential and its throttling counters.</summary>
internal sealed record CredentialRow(
    Guid UserId,
    string Algorithm,
    string Parameters,
    string PasswordHash,
    bool MustChange,
    bool IsTemporary,
    DateTimeOffset? ExpiresAt,
    int FailedAttemptCount,
    DateTimeOffset? LockedUntil,
    int RowVersion);

internal sealed record SessionRow(Guid Id, Guid UserId, DateTimeOffset LastSeenAt, DateTimeOffset ExpiresAt, DateTimeOffset? RevokedAt);

/// <summary>Reads and writes for the authentication module. Nothing here ever returns a hash outside the module.</summary>
internal static class AuthSql
{
    public static Task<CredentialRow?> LoadCredentialAsync(PostgresUnitOfWork unitOfWork, Guid userId, bool forUpdate, CancellationToken cancellationToken) =>
        unitOfWork.Command($"""
                SELECT user_id, algorithm, parameters, password_hash, must_change, is_temporary, expires_at,
                       failed_attempt_count, locked_until, row_version
                FROM rcs.user_credential WHERE user_id = @id{(forUpdate ? " FOR UPDATE" : string.Empty)}
                """)
            .With("id", userId)
            .SingleOrDefaultAsync(reader => new CredentialRow(
                reader.Uuid("user_id"),
                reader.Text("algorithm"),
                reader.Text("parameters"),
                reader.Text("password_hash"),
                reader.Bool("must_change"),
                reader.Bool("is_temporary"),
                reader.InstantOrNull("expires_at"),
                reader.Int("failed_attempt_count"),
                reader.InstantOrNull("locked_until"),
                reader.Int("row_version")), cancellationToken);

    /// <summary>Writes a new or replacement credential. The hash arrives already encoded; this never sees a password.</summary>
    public static Task UpsertCredentialAsync(
        PostgresUnitOfWork unitOfWork,
        Guid userId,
        string algorithm,
        string parameters,
        string encodedHash,
        bool mustChange,
        bool isTemporary,
        DateTimeOffset? expiresAt,
        Guid setBy,
        DateTimeOffset now,
        CancellationToken cancellationToken) =>
        unitOfWork.Command("""
                INSERT INTO rcs.user_credential (
                    user_id, algorithm, parameters, password_hash, must_change, is_temporary, expires_at, set_at, set_by_user_id)
                VALUES (@id, @algorithm, @parameters, @hash, @must_change, @temporary, @expires, @now, @set_by)
                ON CONFLICT (user_id) DO UPDATE SET
                    algorithm = EXCLUDED.algorithm, parameters = EXCLUDED.parameters, password_hash = EXCLUDED.password_hash,
                    must_change = EXCLUDED.must_change, is_temporary = EXCLUDED.is_temporary, expires_at = EXCLUDED.expires_at,
                    set_at = EXCLUDED.set_at, set_by_user_id = EXCLUDED.set_by_user_id,
                    failed_attempt_count = 0, locked_until = NULL,
                    updated_at = EXCLUDED.set_at, row_version = rcs.user_credential.row_version + 1
                """)
            .With("id", userId)
            .With("algorithm", algorithm)
            .With("parameters", parameters)
            .With("hash", encodedHash)
            .With("must_change", mustChange)
            .With("temporary", isTemporary)
            .With("expires", expiresAt)
            .With("now", now)
            .With("set_by", setBy)
            .ExecuteAsync(cancellationToken);

    /// <summary>Re-hash after a successful sign-in when the cost has been raised since (SECURITY.md §6.2).</summary>
    public static Task RehashAsync(PostgresUnitOfWork unitOfWork, Guid userId, string algorithm, string parameters, string encodedHash, DateTimeOffset now, CancellationToken cancellationToken) =>
        unitOfWork.Command("""
                UPDATE rcs.user_credential
                SET algorithm = @algorithm, parameters = @parameters, password_hash = @hash, updated_at = @now, row_version = row_version + 1
                WHERE user_id = @id
                """)
            .With("id", userId)
            .With("algorithm", algorithm)
            .With("parameters", parameters)
            .With("hash", encodedHash)
            .With("now", now)
            .ExecuteAsync(cancellationToken);

    public static Task RecordFailureAsync(PostgresUnitOfWork unitOfWork, Guid userId, int maxAttempts, TimeSpan lockout, DateTimeOffset now, CancellationToken cancellationToken) =>
        unitOfWork.Command("""
                UPDATE rcs.user_credential
                SET failed_attempt_count = CASE WHEN locked_until <= @now THEN 1 ELSE LEAST(failed_attempt_count + 1, @max) END,
                    last_failed_at = @now,
                    locked_until = CASE WHEN locked_until <= @now THEN NULL WHEN failed_attempt_count + 1 >= @max THEN @until ELSE locked_until END,
                    updated_at = @now, row_version = row_version + 1
                WHERE user_id = @id
                """)
            .With("id", userId)
            .With("max", maxAttempts)
            .With("until", now + lockout)
            .With("now", now)
            .ExecuteAsync(cancellationToken);

    public static Task RecordSuccessAsync(PostgresUnitOfWork unitOfWork, Guid userId, DateTimeOffset now, CancellationToken cancellationToken) =>
        unitOfWork.Command("""
                UPDATE rcs.user_credential
                SET failed_attempt_count = 0, locked_until = NULL, last_success_at = @now, updated_at = @now, row_version = row_version + 1
                WHERE user_id = @id
                """)
            .With("id", userId)
            .With("now", now)
            .ExecuteAsync(cancellationToken);

    public static Task ReleaseLockAsync(PostgresUnitOfWork unitOfWork, Guid userId, DateTimeOffset now, CancellationToken cancellationToken) =>
        unitOfWork.Command("""
                UPDATE rcs.user_credential
                SET failed_attempt_count = 0, locked_until = NULL, updated_at = @now, row_version = row_version + 1
                WHERE user_id = @id
                """)
            .With("id", userId)
            .With("now", now)
            .ExecuteAsync(cancellationToken);

    public static Task CreateSessionAsync(PostgresUnitOfWork unitOfWork, Guid sessionId, Guid userId, string? clientHost, DateTimeOffset now, TimeSpan lifetime, CancellationToken cancellationToken) =>
        unitOfWork.Command("""
                INSERT INTO rcs.user_session (id, user_id, created_at, last_seen_at, expires_at, client_host)
                VALUES (@id, @user, @now, @now, @expires, @host)
                """)
            .With("id", sessionId)
            .With("user", userId)
            .With("now", now)
            .With("expires", now + lifetime)
            .With("host", clientHost)
            .ExecuteAsync(cancellationToken);

    public static Task<SessionRow?> LoadSessionAsync(PostgresUnitOfWork unitOfWork, Guid sessionId, CancellationToken cancellationToken) =>
        unitOfWork.Command("SELECT id, user_id, last_seen_at, expires_at, revoked_at FROM rcs.user_session WHERE id = @id")
            .With("id", sessionId)
            .SingleOrDefaultAsync(reader => new SessionRow(
                reader.Uuid("id"),
                reader.Uuid("user_id"),
                reader.Instant("last_seen_at"),
                reader.Instant("expires_at"),
                reader.InstantOrNull("revoked_at")), cancellationToken);

    public static Task TouchSessionAsync(PostgresUnitOfWork unitOfWork, Guid sessionId, DateTimeOffset now, CancellationToken cancellationToken) =>
        unitOfWork.Command("""
                UPDATE rcs.user_session SET last_seen_at = @now, row_version = row_version + 1
                WHERE id = @id AND revoked_at IS NULL
                """)
            .With("id", sessionId)
            .With("now", now)
            .ExecuteAsync(cancellationToken);

    public static Task RevokeSessionAsync(PostgresUnitOfWork unitOfWork, Guid sessionId, string reason, DateTimeOffset now, CancellationToken cancellationToken) =>
        unitOfWork.Command("""
                UPDATE rcs.user_session SET revoked_at = @now, revocation_reason = @reason, row_version = row_version + 1
                WHERE id = @id AND revoked_at IS NULL
                """)
            .With("id", sessionId)
            .With("reason", reason)
            .With("now", now)
            .ExecuteAsync(cancellationToken);

    /// <summary>Ends every live session of one account at once — suspension, password change, administrative reset.</summary>
    public static Task<int> RevokeUserSessionsAsync(PostgresUnitOfWork unitOfWork, Guid userId, string reason, DateTimeOffset now, Guid? exceptSessionId, CancellationToken cancellationToken) =>
        unitOfWork.Command("""
                UPDATE rcs.user_session SET revoked_at = @now, revocation_reason = @reason, row_version = row_version + 1
                WHERE user_id = @user AND revoked_at IS NULL AND (@except::uuid IS NULL OR id <> @except::uuid)
                """)
            .With("user", userId)
            .With("reason", reason)
            .With("now", now)
            .With("except", exceptSessionId)
            .ExecuteAsync(cancellationToken);

    public static Task<Guid?> FindUserIdAsync(PostgresUnitOfWork unitOfWork, string username, CancellationToken cancellationToken) =>
        unitOfWork.Command("SELECT id FROM rcs.app_user WHERE username = @username")
            .With("username", username)
            .ScalarAsync<Guid?>(cancellationToken);

    public static Task<bool> HasActiveResponsibilitiesAsync(PostgresUnitOfWork unitOfWork, Guid userId, DateTimeOffset now, CancellationToken cancellationToken) =>
        unitOfWork.Command("""
                SELECT EXISTS (
                    SELECT 1 FROM rcs.assignment AS a
                    JOIN rcs.assignment_role AS r ON r.id = a.assignment_role_id
                    WHERE a.assignee_user_id = @user AND a.status = 'ACTIVE' AND r.code = 'RESPONSIBLE'
                      AND a.valid_from <= @now AND (a.valid_until IS NULL OR a.valid_until > @now))
                """)
            .With("user", userId)
            .With("now", now)
            .ScalarAsync<bool>(cancellationToken)!;

    public static string RoleId(BusinessRole role) => role switch
    {
        BusinessRole.Worker => "01995c00-0001-7000-8000-000000000001",
        BusinessRole.Chief => "01995c00-0001-7000-8000-000000000002",
        BusinessRole.Head => "01995c00-0001-7000-8000-000000000003",
        BusinessRole.TechAdmin => "01995c00-0001-7000-8000-000000000004",
        _ => throw new ArgumentOutOfRangeException(nameof(role), role, "Unknown role."),
    };

    public static NpgsqlCommand WithRole(this NpgsqlCommand command, string name, BusinessRole role) =>
        command.With(name, Guid.Parse(RoleId(role)));
}
