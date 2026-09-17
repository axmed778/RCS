using Npgsql;
using Rcs.Infrastructure.Migrations;
using Rcs.IntegrationTests.TestSupport;

namespace Rcs.IntegrationTests.Migrations;

public sealed class MigrationLockTests : IAsyncLifetime
{
    private TestDatabase database = null!;

    public async ValueTask InitializeAsync() => database = await TestDatabase.CreateAsync();

    public async ValueTask DisposeAsync() => await database.DisposeAsync();

    [Fact]
    public async Task RunnerTimesOutWithoutTouchingTheSchemaWhileAnotherHolderHasTheLock()
    {
        await using var holder = new NpgsqlConnection(database.AdminConnectionString);
        await holder.OpenAsync();
        await using (var take = new NpgsqlCommand($"SELECT pg_advisory_lock({MigrationRunner.MigrationLockKey})", holder))
        {
            await take.ExecuteScalarAsync();
        }

        await Assert.ThrowsAsync<MigrationLockTimeoutException>(() => database.MigrateAsync(lockTimeout: TimeSpan.FromSeconds(1.5)));
        Assert.Null(await database.ScalarAsync<string?>("SELECT to_regnamespace('rcs')::text")); // not even the bootstrap ran

        await using (var release = new NpgsqlCommand($"SELECT pg_advisory_unlock({MigrationRunner.MigrationLockKey})", holder))
        {
            await release.ExecuteScalarAsync();
        }

        var result = await database.MigrateAsync(lockTimeout: TimeSpan.FromSeconds(5));
        Assert.NotEmpty(result.Applied);
    }

    [Fact]
    public async Task ConcurrentRunnersApplyEachMigrationExactlyOnce()
    {
        var release = MigrationSet.LoadEmbedded();

        var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => database.MigrateAsync(release, TimeSpan.FromSeconds(60))));

        Assert.Equal(release.Migrations.Count, results.Sum(result => result.Applied.Count));
        Assert.Equal((long)release.Migrations.Count, await database.ScalarAsync<long>("SELECT count(*) FROM rcs.schema_migration"));
    }

    [Fact]
    public async Task LockIsReleasedAfterAFailedRun()
    {
        var failing = Releases.RealPlus(Releases.Migration(2, "fails", "SELECT 1 / 0;"));
        await Assert.ThrowsAsync<MigrationExecutionException>(() => database.MigrateAsync(failing));

        Assert.True(await database.ScalarAsync<bool>($"SELECT pg_try_advisory_lock({MigrationRunner.MigrationLockKey})"));
    }
}
