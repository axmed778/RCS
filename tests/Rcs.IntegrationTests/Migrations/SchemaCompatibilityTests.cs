using Npgsql;
using Rcs.Infrastructure.Migrations;
using Rcs.IntegrationTests.TestSupport;

namespace Rcs.IntegrationTests.Migrations;

public sealed class SchemaCompatibilityTests : IAsyncLifetime
{
    private TestDatabase database = null!;

    public async ValueTask InitializeAsync() => database = await TestDatabase.CreateAsync();

    public async ValueTask DisposeAsync() => await database.DisposeAsync();

    private static async Task<SchemaCompatibilityReport> CheckAsync(string connectionString, MigrationSet release)
    {
        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        return await new SchemaCompatibilityChecker(dataSource, release).CheckAsync();
    }

    [Fact]
    public async Task UnmigratedDatabaseIsNotInitialized()
    {
        var report = await CheckAsync(database.RuntimeConnectionString, MigrationSet.LoadEmbedded());
        Assert.Equal(SchemaCompatibilityStatus.NotInitialized, report.Status);
    }

    [Fact]
    public async Task ExactlyMigratedDatabaseIsCompatible()
    {
        await database.MigrateAsync();

        var report = await CheckAsync(database.RuntimeConnectionString, MigrationSet.LoadEmbedded());

        Assert.True(report.IsCompatible, report.Message);
        Assert.Equal(MigrationSet.LoadEmbedded().LatestVersion, report.DatabaseVersion);
    }

    [Fact]
    public async Task DatabaseBehindTheReleaseIsRejected()
    {
        await database.MigrateAsync();
        var newerRelease = Releases.RealPlus(Releases.Migration(Releases.Next(), "newer", "CREATE TABLE rcs.it_newer (id integer);"));

        var report = await CheckAsync(database.RuntimeConnectionString, newerRelease);

        Assert.Equal(SchemaCompatibilityStatus.DatabaseBehind, report.Status);
        Assert.Equal(MigrationSet.LoadEmbedded().LatestVersion, report.DatabaseVersion);
        Assert.Equal(Releases.Next(), report.ExpectedVersion);
    }

    [Fact]
    public async Task DatabaseAheadOfTheReleaseIsRejected()
    {
        await database.MigrateAsync(Releases.RealPlus(Releases.Migration(Releases.Next(), "newer", "CREATE TABLE rcs.it_newer (id integer);")));

        var report = await CheckAsync(database.RuntimeConnectionString, MigrationSet.LoadEmbedded());

        Assert.Equal(SchemaCompatibilityStatus.DatabaseAhead, report.Status);
    }

    [Fact]
    public async Task EditedMigrationMakesTheSchemaIncompatible()
    {
        await database.MigrateAsync();
        var tampered = Releases.WithRealFileEdited("0001_foundation.sql", content => content + "\n-- edit\n");

        var report = await CheckAsync(database.RuntimeConnectionString, tampered);

        Assert.Equal(SchemaCompatibilityStatus.HistoryMismatch, report.Status);
    }

    [Fact]
    public async Task RuntimeRoleThatCannotReadHistoryIsReported()
    {
        await database.MigrateAsync();
        await database.ExecuteAsync("REVOKE SELECT ON rcs.schema_migration FROM rcs_app", database.MigrationConnectionString);

        var report = await CheckAsync(database.RuntimeConnectionString, MigrationSet.LoadEmbedded());

        Assert.Equal(SchemaCompatibilityStatus.HistoryUnreadable, report.Status);
    }

    [Fact]
    public async Task SchemaOwnerOrSuperuserIsNeverAcceptedAsTheRuntimeRole()
    {
        await database.MigrateAsync();
        var release = MigrationSet.LoadEmbedded();

        Assert.Equal(SchemaCompatibilityStatus.UnsafeRuntimeRole, (await CheckAsync(database.MigrationConnectionString, release)).Status);
        Assert.Equal(SchemaCompatibilityStatus.UnsafeRuntimeRole, (await CheckAsync(database.AdminConnectionString, release)).Status);
    }
}
