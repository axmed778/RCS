using Npgsql;
using Rcs.Application.Identity;
using Rcs.Domain.Authorization;
using Rcs.Domain.Vocabulary;
using Rcs.Infrastructure.Persistence;

namespace Rcs.Infrastructure.Identity;

/// <summary>Reads a user with the roles valid at a given instant — authority is always evaluated at action time (PERMISSIONS.md §27.3).</summary>
internal static class ActorStore
{
    // The predicate is chosen by the caller from the two fixed forms below — never built from user input.
    private const string ProfileSqlPrefix = """
        SELECT u.id, u.username, u.display_name, u.job_title, u.status,
               COALESCE(array_agg(r.code) FILTER (WHERE r.code IS NOT NULL), ARRAY[]::text[]) AS roles
        FROM rcs.app_user AS u
        LEFT JOIN rcs.user_role AS ur
               ON ur.user_id = u.id AND ur.valid_from <= @now AND (ur.valid_until IS NULL OR ur.valid_until > @now)
        LEFT JOIN rcs.role AS r ON r.id = ur.role_id AND r.is_active
        WHERE
        """;

    public const string ById = "u.id = @id";

    public const string ByUsername = "u.username = @username";

    public static string ProfileQuery(string predicate) =>
        predicate is ById or ByUsername
            ? $"{ProfileSqlPrefix} {predicate} GROUP BY u.id"
            : throw new ArgumentException("Unknown actor predicate.", nameof(predicate));

    public static ActorProfile Map(NpgsqlDataReader reader) => new(
        reader.Uuid("id"),
        reader.Text("username"),
        reader.Text("display_name"),
        reader.TextOrNull("job_title"),
        VocabularyCodes.FromCode<UserStatus>(reader.Text("status")),
        reader.TextArray("roles").Select(VocabularyCodes.FromCode<BusinessRole>).ToHashSet());

    public static Task<ActorProfile?> LoadAsync(PostgresUnitOfWork unitOfWork, Guid userId, DateTimeOffset now, CancellationToken cancellationToken) =>
        unitOfWork.Command(ProfileQuery(ById)).With("id", userId).With("now", now).SingleOrDefaultAsync(Map, cancellationToken);
}

internal sealed class PostgresUserDirectory(NpgsqlDataSource dataSource, TimeProvider timeProvider) : IUserDirectory
{
    public async Task<ActorProfile?> FindByUsernameAsync(string username, CancellationToken cancellationToken = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(ActorStore.ProfileQuery(ActorStore.ByUsername), connection);
        command.With("username", username).With("now", timeProvider.GetUtcNow());
        return await Read(command, cancellationToken);
    }

    public async Task<ActorProfile?> GetAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(ActorStore.ProfileQuery(ActorStore.ById), connection);
        command.With("id", userId).With("now", timeProvider.GetUtcNow());
        return await Read(command, cancellationToken);
    }

    public async Task<IReadOnlyList<UserOption>> ListAssignableAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            SELECT DISTINCT u.id, u.display_name, u.job_title
            FROM rcs.app_user AS u
            JOIN rcs.user_role AS ur ON ur.user_id = u.id AND ur.valid_from <= @now AND (ur.valid_until IS NULL OR ur.valid_until > @now)
            JOIN rcs.role AS r ON r.id = ur.role_id AND r.is_active AND r.code IN ('WORKER', 'CHIEF', 'HEAD')
            WHERE u.status = 'ACTIVE'
            """, connection);
        command.With("now", timeProvider.GetUtcNow());
        var users = await command.ListAsync(reader => new UserOption(reader.Uuid("id"), reader.Text("display_name"), reader.TextOrNull("job_title")), cancellationToken);
        return users.OrderBy(user => user.DisplayName, DisplayOrder.Comparer).ToArray();
    }

    private static async Task<ActorProfile?> Read(NpgsqlCommand command, CancellationToken cancellationToken)
    {
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ActorStore.Map(reader) : null;
    }
}

/// <summary>
/// Display ordering of names, applied in the application for presentation only. It deliberately does not rely on a
/// database collation: the production collation is undecided (PS-1).
/// </summary>
internal static class DisplayOrder
{
    public static readonly StringComparer Comparer = StringComparer.Create(System.Globalization.CultureInfo.GetCultureInfo("az-Latn-AZ"), ignoreCase: true);
}
