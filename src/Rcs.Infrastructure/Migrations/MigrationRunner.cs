using System.Diagnostics;
using System.Reflection;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Rcs.Infrastructure.Migrations;

public sealed class MigrationRunnerOptions
{
    /// <summary>How long to wait for another runner to release the migration lock before failing.</summary>
    public TimeSpan LockTimeout { get; init; } = TimeSpan.FromSeconds(60);
}

public sealed record AppliedMigration(int Id, string FileName, bool Transactional, TimeSpan Duration);

public sealed record MigrationRunResult(IReadOnlyList<AppliedMigration> Applied, int SchemaVersion);

/// <summary>
/// Applies hand-written SQL migrations as an explicit deployment action (DECISIONS.md ADR-004,
/// ARCHITECTURE.md §7.5). Never called by normal application startup.
/// </summary>
/// <remarks>
/// <list type="number">
/// <item>Open one <b>unpooled</b> connection as the migration role.</item>
/// <item>Take the session advisory lock <see cref="MigrationLockKey"/>, polling until
/// <see cref="MigrationRunnerOptions.LockTimeout"/>; then fail without touching the schema.</item>
/// <item>Run the idempotent bootstrap (schema <c>rcs</c> and <c>rcs.schema_migration</c>).</item>
/// <item>Compare history with the release. Any edited, renamed or unknown applied migration stops the run
/// before anything executes.</item>
/// <item>Apply pending migrations in order: each transactional migration and its history row commit
/// together. On the first failure, stop; the failed migration is not recorded.</item>
/// <item>Release the lock. Pooling is disabled, so closing the connection releases the lock even if the
/// explicit unlock cannot run.</item>
/// </list>
/// </remarks>
public sealed class MigrationRunner(ILogger<MigrationRunner> logger)
{
    /// <summary>Session advisory lock key reserved for schema migration: the ASCII bytes of "RCS_MIGR".</summary>
    public const long MigrationLockKey = 0x5243_535F_4D49_4752;

    private static readonly TimeSpan LockPollInterval = TimeSpan.FromMilliseconds(250);

    private static readonly string RunnerVersion =
        typeof(MigrationRunner).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? "unknown";

    public async Task<MigrationRunResult> RunAsync(
        string migrationConnectionString,
        MigrationSet migrations,
        MigrationRunnerOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(migrationConnectionString);
        ArgumentNullException.ThrowIfNull(migrations);
        ArgumentNullException.ThrowIfNull(options);

        var connectionString = new NpgsqlConnectionStringBuilder(migrationConnectionString)
        {
            Pooling = false,   // the session lock must die with this physical connection
            CommandTimeout = 0, // migrations are attended deployment steps; do not cut one off halfway
        }.ConnectionString;

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        await AcquireLockAsync(connection, options.LockTimeout, cancellationToken);
        try
        {
            await BootstrapAsync(connection, cancellationToken);

            var history = await MigrationHistory.ReadAsync(connection, transaction: null, cancellationToken);
            var comparison = MigrationHistory.Compare(history, migrations);
            var problems = comparison.Mismatches
                .Concat(comparison.NotInRelease.Select(applied =>
                    $"migration {applied.MigrationId:D4}_{applied.Name} is applied in the database but is not part of this release: the database is ahead of this release."))
                .ToArray();
            if (problems.Length > 0)
            {
                throw new MigrationHistoryMismatchException(problems);
            }

            if (comparison.Pending.Count == 0)
            {
                logger.LogInformation("Database schema is up to date at version {SchemaVersion}.", migrations.LatestVersion);
            }

            var applied = new List<AppliedMigration>();
            foreach (var migration in comparison.Pending)
            {
                applied.Add(await ApplyAsync(connection, migration, cancellationToken));
            }

            return new MigrationRunResult(applied, migrations.LatestVersion);
        }
        finally
        {
            await ReleaseLockAsync(connection);
        }
    }

    private async Task AcquireLockAsync(NpgsqlConnection connection, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var waited = Stopwatch.StartNew();
        var announcedWait = false;
        while (true)
        {
            await using (var command = new NpgsqlCommand("SELECT pg_try_advisory_lock($1)", connection))
            {
                command.Parameters.Add(new NpgsqlParameter<long> { TypedValue = MigrationLockKey });
                if (await command.ExecuteScalarAsync(cancellationToken) is true)
                {
                    logger.LogDebug("Migration lock acquired.");
                    return;
                }
            }

            var remaining = timeout - waited.Elapsed;
            if (remaining <= TimeSpan.Zero)
            {
                throw new MigrationLockTimeoutException(timeout);
            }

            if (!announcedWait)
            {
                logger.LogWarning("Another migration runner holds the migration lock; waiting up to {LockTimeout}.", timeout);
                announcedWait = true;
            }

            await Task.Delay(remaining < LockPollInterval ? remaining : LockPollInterval, cancellationToken);
        }
    }

    private async Task ReleaseLockAsync(NpgsqlConnection connection)
    {
        if (connection.State != System.Data.ConnectionState.Open)
        {
            return;
        }

        try
        {
            await using var command = new NpgsqlCommand("SELECT pg_advisory_unlock($1)", connection);
            command.Parameters.Add(new NpgsqlParameter<long> { TypedValue = MigrationLockKey });
            await command.ExecuteScalarAsync(CancellationToken.None);
        }
        catch (Exception exception) when (exception is NpgsqlException or InvalidOperationException)
        {
            logger.LogWarning(exception, "The migration lock could not be released explicitly; closing the unpooled connection releases it.");
        }
    }

    private static async Task BootstrapAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        var sql = MigrationSet.ReadEmbedded(MigrationSet.EmbeddedBootstrapName);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            await using (var command = new NpgsqlCommand(sql, connection, transaction))
            {
                await command.ExecuteNonQueryAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
        }
        catch (PostgresException exception)
        {
            throw new MigrationPrerequisiteException(
                $"The migration bootstrap failed ({exception.SqlState}: {exception.MessageText}). Check that database/roles/roles.sql has been run and that the database is owned by the migration role (database/README.md).",
                exception);
        }
    }

    private async Task<AppliedMigration> ApplyAsync(NpgsqlConnection connection, Migration migration, CancellationToken cancellationToken)
    {
        logger.LogInformation("Applying migration {FileName}: {Description}", migration.FileName, migration.Description);
        var stopwatch = Stopwatch.StartNew();

        if (migration.Transactional)
        {
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            try
            {
                await ExecuteAsync(connection, transaction, migration.Sql, cancellationToken);
                await RecordAsync(connection, transaction, migration, stopwatch.ElapsedMilliseconds, cancellationToken);
                await transaction.CommitAsync(cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // Disposing the uncommitted transaction rolls back both the migration and its history row.
                throw new MigrationExecutionException(migration, exception);
            }
        }
        else
        {
            // Escape hatch for statements PostgreSQL refuses inside a transaction block (for example
            // CREATE INDEX CONCURRENTLY). Such a file must hold a single statement and must be safe to
            // re-run, because a failure after the statement but before the history row leaves it unrecorded.
            try
            {
                await ExecuteAsync(connection, transaction: null, migration.Sql, cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                throw new MigrationExecutionException(migration, exception);
            }

            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            await RecordAsync(connection, transaction, migration, stopwatch.ElapsedMilliseconds, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }

        stopwatch.Stop();
        logger.LogInformation("Applied migration {FileName} in {ElapsedMilliseconds} ms.", migration.FileName, stopwatch.ElapsedMilliseconds);
        return new AppliedMigration(migration.Id, migration.FileName, migration.Transactional, stopwatch.Elapsed);
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction, string sql, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task RecordAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Migration migration,
        long elapsedMilliseconds,
        CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO rcs.schema_migration
                (migration_id, name, description, checksum_sha256, transactional, execution_ms, runner_version)
            VALUES ($1, $2, $3, $4, $5, $6, $7)
            """;

        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.Add(new NpgsqlParameter<int> { TypedValue = migration.Id });
        command.Parameters.Add(new NpgsqlParameter<string> { TypedValue = migration.Name });
        command.Parameters.Add(new NpgsqlParameter<string> { TypedValue = migration.Description });
        command.Parameters.Add(new NpgsqlParameter<string> { TypedValue = migration.Checksum });
        command.Parameters.Add(new NpgsqlParameter<bool> { TypedValue = migration.Transactional });
        command.Parameters.Add(new NpgsqlParameter<long> { TypedValue = elapsedMilliseconds });
        command.Parameters.Add(new NpgsqlParameter<string> { TypedValue = RunnerVersion });
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
