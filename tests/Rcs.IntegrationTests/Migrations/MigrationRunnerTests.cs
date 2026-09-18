using Npgsql;
using Rcs.Infrastructure.Migrations;
using Rcs.IntegrationTests.TestSupport;

namespace Rcs.IntegrationTests.Migrations;

public sealed class MigrationRunnerTests : IAsyncLifetime
{
    private TestDatabase database = null!;

    public async ValueTask InitializeAsync() => database = await TestDatabase.CreateAsync();

    public async ValueTask DisposeAsync() => await database.DisposeAsync();

    [Fact]
    public async Task CleanDatabaseMigratesFromZeroAndRecordsHistory()
    {
        var release = MigrationSet.LoadEmbedded();

        var result = await database.MigrateAsync();

        Assert.Equal(release.LatestVersion, result.SchemaVersion);
        Assert.Equal(release.Migrations.Select(m => m.Id), result.Applied.Select(a => a.Id));
        Assert.Equal((long)release.Migrations.Count, await database.ScalarAsync<long>("SELECT count(*) FROM rcs.schema_migration"));
        Assert.Equal(release.Migrations[0].Checksum, await database.ScalarAsync<string>("SELECT checksum_sha256 FROM rcs.schema_migration WHERE migration_id = 1"));
        Assert.Equal("rcs_migrate", await database.ScalarAsync<string>("SELECT applied_by FROM rcs.schema_migration WHERE migration_id = 1"));
    }

    [Fact]
    public async Task SecondRunAppliesNothingAndLeavesHistoryUntouched()
    {
        await database.MigrateAsync();
        var historyBefore = await database.ScalarAsync<string>("SELECT string_agg(migration_id || ':' || applied_at, ',' ORDER BY migration_id) FROM rcs.schema_migration");

        var second = await database.MigrateAsync();

        Assert.Empty(second.Applied);
        Assert.Equal(historyBefore, await database.ScalarAsync<string>("SELECT string_agg(migration_id || ':' || applied_at, ',' ORDER BY migration_id) FROM rcs.schema_migration"));
    }

    [Fact]
    public async Task EditedAppliedMigrationIsDetectedAndNothingRuns()
    {
        await database.MigrateAsync();
        var tampered = Releases.WithRealFileEdited("0001_foundation.sql", content => content + "\n-- an innocent-looking edit\n");
        var withNewMigration = MigrationSet.Create(
            tampered.Migrations.Select(m => (m.FileName, m.Sql)).Append(Releases.Migration(Releases.Next(), "must_not_run", "CREATE TABLE rcs.it_must_not_run (id integer);")));

        var exception = await Assert.ThrowsAsync<MigrationHistoryMismatchException>(() => database.MigrateAsync(withNewMigration));

        Assert.Contains(exception.Problems, problem => problem.Contains("modified after it was applied", StringComparison.Ordinal));
        Assert.Equal((long)MigrationSet.LoadEmbedded().Migrations.Count, await database.ScalarAsync<long>("SELECT count(*) FROM rcs.schema_migration"));
        Assert.Null(await database.ScalarAsync<string?>("SELECT to_regclass('rcs.it_must_not_run')::text"));
    }

    [Fact]
    public async Task DatabaseAheadOfTheReleaseIsRejected()
    {
        await database.MigrateAsync(Releases.RealPlus(Releases.Migration(Releases.Next(), "newer_release", "CREATE TABLE rcs.it_newer (id integer);")));

        var exception = await Assert.ThrowsAsync<MigrationHistoryMismatchException>(() => database.MigrateAsync());

        Assert.Contains(exception.Problems, problem => problem.Contains("database is ahead", StringComparison.Ordinal));
    }

    [Fact]
    public async Task FailingMigrationIsNotRecordedAndLaterMigrationsDoNotRun()
    {
        var release = Releases.RealPlus(
            Releases.Migration(Releases.Next(), "probe_a", "CREATE TABLE rcs.it_probe_a (id integer);"),
            Releases.Migration(Releases.Next(1), "fails", "CREATE TABLE rcs.it_probe_b (id integer);\nSELECT 1 / 0;"),
            Releases.Migration(Releases.Next(2), "after_failure", "CREATE TABLE rcs.it_probe_c (id integer);"));

        var exception = await Assert.ThrowsAsync<MigrationExecutionException>(() => database.MigrateAsync(release));

        Assert.Equal(Releases.Next(1), exception.MigrationId);
        Assert.IsType<PostgresException>(exception.InnerException);
        Assert.Equal(Releases.Next(), await database.ScalarAsync<int>("SELECT max(migration_id) FROM rcs.schema_migration"));
        Assert.Null(await database.ScalarAsync<string?>("SELECT to_regclass('rcs.it_probe_b')::text")); // rolled back
        Assert.Null(await database.ScalarAsync<string?>("SELECT to_regclass('rcs.it_probe_c')::text")); // never attempted

        // A migration that never applied may still be fixed; the next run continues from where it stopped.
        var fixedRelease = Releases.RealPlus(
            Releases.Migration(Releases.Next(), "probe_a", "CREATE TABLE rcs.it_probe_a (id integer);"),
            Releases.Migration(Releases.Next(1), "fails", "CREATE TABLE rcs.it_probe_b (id integer);"),
            Releases.Migration(Releases.Next(2), "after_failure", "CREATE TABLE rcs.it_probe_c (id integer);"));
        var rerun = await database.MigrateAsync(fixedRelease);

        Assert.Equal([Releases.Next(1), Releases.Next(2)], rerun.Applied.Select(applied => applied.Id));
    }

    [Fact]
    public async Task NonTransactionalEscapeHatchRunsStatementsPostgresRefusesInATransaction()
    {
        var release = Releases.RealPlus(
            Releases.Migration(Releases.Next(), "probe_table", "CREATE TABLE rcs.it_probe (id integer);"),
            Releases.Migration(Releases.Next(1), "probe_index", "CREATE INDEX CONCURRENTLY IF NOT EXISTS it_probe_idx ON rcs.it_probe (id);", transactional: false));

        var result = await database.MigrateAsync(release);

        Assert.False(result.Applied.Single(applied => applied.Id == Releases.Next(1)).Transactional);
        Assert.False(await database.ScalarAsync<bool>($"SELECT transactional FROM rcs.schema_migration WHERE migration_id = {Releases.Next(1)}"));
        Assert.Equal(1L, await database.ScalarAsync<long>("SELECT count(*) FROM pg_indexes WHERE schemaname = 'rcs' AND indexname = 'it_probe_idx'"));
    }

    [Fact]
    public async Task BootstrapFailsClearlyWhenTheDatabaseIsNotOwnedByTheMigrationRole()
    {
        // Owned by the administrator: rcs_migrate has no CREATE privilege on it, so the bootstrap cannot create the schema.
        await using var notOwned = await TestDatabase.CreateAsync(ownedByMigrationRole: false);

        await Assert.ThrowsAsync<MigrationPrerequisiteException>(() => notOwned.MigrateAsync());
    }
}
