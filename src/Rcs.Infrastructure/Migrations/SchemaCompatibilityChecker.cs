using Npgsql;

namespace Rcs.Infrastructure.Migrations;

public enum SchemaCompatibilityStatus
{
    /// <summary>Applied history equals this release exactly — same numbers, names and checksums.</summary>
    Compatible = 1,

    /// <summary>No migration history: the database was never migrated.</summary>
    NotInitialized,

    /// <summary>The release has migrations the database has not applied. Run <c>migrate</c>.</summary>
    DatabaseBehind,

    /// <summary>The database has migrations this release does not know. Deploy the matching release.</summary>
    DatabaseAhead,

    /// <summary>An applied migration was edited or renamed, or history has gaps.</summary>
    HistoryMismatch,

    /// <summary>The runtime role cannot read migration history.</summary>
    HistoryUnreadable,

    /// <summary>The runtime connection is a superuser, owns the schema, or can create objects in it.</summary>
    UnsafeRuntimeRole,
}

public sealed record SchemaCompatibilityReport(
    SchemaCompatibilityStatus Status,
    int ExpectedVersion,
    int? DatabaseVersion,
    string Message)
{
    public bool IsCompatible => Status == SchemaCompatibilityStatus.Compatible;
}

/// <summary>Thrown by application startup when the schema is not exactly the one this release expects.</summary>
public sealed class SchemaIncompatibleException(SchemaCompatibilityReport report)
    : Exception($"Database schema is not compatible with this release ({report.Status}): {report.Message}")
{
    public SchemaCompatibilityReport Report { get; } = report;
}

/// <summary>
/// Read-only check that the database schema is exactly the one this release expects, run by the
/// application with its own runtime credentials. It never changes the schema.
/// </summary>
public sealed class SchemaCompatibilityChecker(NpgsqlDataSource dataSource, MigrationSet release)
{
    private const string RuntimeRoleSql = """
        SELECT r.rolsuper,
               COALESCE(n.nspowner = r.oid, false) AS owns_schema,
               COALESCE(has_schema_privilege(r.oid, n.oid, 'CREATE'), false) AS can_create_in_schema
        FROM pg_catalog.pg_roles AS r
        LEFT JOIN pg_catalog.pg_namespace AS n ON n.nspname = 'rcs'
        WHERE r.rolname = current_user
        """;

    public async Task<SchemaCompatibilityReport> CheckAsync(CancellationToken cancellationToken = default)
    {
        var expected = release.LatestVersion;
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);

        var unsafeRole = await DescribeUnsafeRuntimeRoleAsync(connection, cancellationToken);
        if (unsafeRole is not null)
        {
            return new SchemaCompatibilityReport(SchemaCompatibilityStatus.UnsafeRuntimeRole, expected, null, unsafeRole);
        }

        IReadOnlyList<AppliedMigrationRecord> history;
        try
        {
            history = await MigrationHistory.ReadAsync(connection, transaction: null, cancellationToken);
        }
        catch (PostgresException exception) when (exception.SqlState is PostgresErrorCodes.UndefinedTable or PostgresErrorCodes.InvalidSchemaName)
        {
            return new SchemaCompatibilityReport(SchemaCompatibilityStatus.NotInitialized, expected, null,
                "The database has no migration history. Run the migrate command with the migration credentials.");
        }
        catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.InsufficientPrivilege)
        {
            return new SchemaCompatibilityReport(SchemaCompatibilityStatus.HistoryUnreadable, expected, null,
                "The runtime role cannot read rcs.schema_migration. The bootstrap grants SELECT to rcs_app; check the grants.");
        }

        var comparison = MigrationHistory.Compare(history, release);
        if (comparison.Mismatches.Count > 0)
        {
            return new SchemaCompatibilityReport(SchemaCompatibilityStatus.HistoryMismatch, expected, comparison.DatabaseVersion,
                string.Join(" ", comparison.Mismatches));
        }

        if (comparison.NotInRelease.Count > 0)
        {
            return new SchemaCompatibilityReport(SchemaCompatibilityStatus.DatabaseAhead, expected, comparison.DatabaseVersion,
                $"The database is at version {comparison.DatabaseVersion} but this release expects {expected}. Deploy the release that matches the database; never roll the schema back by hand.");
        }

        if (comparison.Pending.Count > 0)
        {
            return new SchemaCompatibilityReport(SchemaCompatibilityStatus.DatabaseBehind, expected, comparison.DatabaseVersion,
                $"The database is at version {comparison.DatabaseVersion} but this release expects {expected}. Run the migrate command before starting the application.");
        }

        return new SchemaCompatibilityReport(SchemaCompatibilityStatus.Compatible, expected, comparison.DatabaseVersion,
            $"The database is at version {comparison.DatabaseVersion}, as this release expects.");
    }

    private static async Task<string?> DescribeUnsafeRuntimeRoleAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(RuntimeRoleSql, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return "The current database role could not be inspected.";
        }

        if (reader.GetBoolean(0))
        {
            return "The runtime connection is a PostgreSQL superuser. The application must run as rcs_app (SECURITY.md invariant 12).";
        }

        if (reader.GetBoolean(1))
        {
            return "The runtime connection owns the rcs schema. Schema ownership belongs to the migration role only.";
        }

        return reader.GetBoolean(2)
            ? "The runtime connection can create objects in the rcs schema. Only the migration role may change the schema."
            : null;
    }
}
