using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Rcs.Infrastructure.Migrations;
using Rcs.IntegrationTests.TestSupport;
using Rcs.Web.Hosting;

namespace Rcs.IntegrationTests.Web;

public sealed class WebHostTests : IAsyncLifetime
{
    private TestDatabase database = null!;

    public async ValueTask InitializeAsync() => database = await TestDatabase.CreateAsync();

    public async ValueTask DisposeAsync() => await database.DisposeAsync();

    private sealed class RcsWebFactory(string runtimeConnectionString, IReadOnlyDictionary<string, string?>? overrides = null)
        : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("IntegrationTest");
            builder.ConfigureAppConfiguration((_, configuration) =>
            {
                var settings = new Dictionary<string, string?> { ["ConnectionStrings:Runtime"] = runtimeConnectionString };
                foreach (var (key, value) in overrides ?? new Dictionary<string, string?>())
                {
                    settings[key] = value;
                }

                configuration.AddInMemoryCollection(settings);
            });
        }
    }

    private static Exception Innermost<TException>(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is TException)
            {
                return current;
            }
        }

        return exception;
    }

    [Fact]
    public async Task StartupFailsWhenTheSchemaIsNotMigrated()
    {
        await using var factory = new RcsWebFactory(database.RuntimeConnectionString);

        var exception = Record.Exception(() => factory.CreateClient());

        Assert.NotNull(exception);
        var incompatible = Assert.IsType<SchemaIncompatibleException>(Innermost<SchemaIncompatibleException>(exception));
        Assert.Equal(SchemaCompatibilityStatus.NotInitialized, incompatible.Report.Status);
    }

    [Fact]
    public async Task StartupFailsWhenTheDatabaseIsBehindTheRelease()
    {
        await database.MigrateAsync();
        await database.ExecuteAsync("DELETE FROM rcs.schema_migration WHERE migration_id = 1", database.MigrationConnectionString);
        await using var factory = new RcsWebFactory(database.RuntimeConnectionString);

        var exception = Record.Exception(() => factory.CreateClient());

        var incompatible = Assert.IsType<SchemaIncompatibleException>(Innermost<SchemaIncompatibleException>(exception!));
        Assert.Equal(SchemaCompatibilityStatus.DatabaseBehind, incompatible.Report.Status);
    }

    [Fact]
    public async Task StartupFailsWhenReleaseConfigurationDisagreesWithTheEmbeddedMigrations()
    {
        await database.MigrateAsync();
        await using var factory = new RcsWebFactory(
            database.RuntimeConnectionString,
            new Dictionary<string, string?> { ["Rcs:Database:ExpectedSchemaVersion"] = "999" });

        var exception = Record.Exception(() => factory.CreateClient());

        Assert.IsType<InvalidOperationException>(Innermost<InvalidOperationException>(exception!));
    }

    [Fact]
    public async Task CompatibleSchemaStartsAndExposesOnlyTechnicalEndpoints()
    {
        await database.MigrateAsync();
        await using var factory = new RcsWebFactory(database.RuntimeConnectionString);
        using var client = factory.CreateClient();

        var live = await client.GetAsync(new Uri("/health/live", UriKind.Relative));
        var ready = await client.GetAsync(new Uri("/health/ready", UriKind.Relative));
        var root = await client.GetAsync(new Uri("/", UriKind.Relative));

        Assert.Equal(HttpStatusCode.OK, live.StatusCode);
        Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
        Assert.Equal("Healthy", await ready.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.NotFound, root.StatusCode); // no business UI in Phase 1
    }

    [Fact]
    public async Task MigrateAndCheckSchemaCommandsAreExplicitDeploymentActions()
    {
        Assert.Equal(ExitCodes.SchemaIncompatible, await RcsEntryPoint.RunAsync(["check-schema", $"--ConnectionStrings:Runtime={database.RuntimeConnectionString}"]));
        Assert.Equal(ExitCodes.UsageOrConfigurationError, await RcsEntryPoint.RunAsync(["migrate"])); // no migration credentials configured

        Assert.Equal(ExitCodes.Success, await RcsEntryPoint.RunAsync(["migrate", $"--ConnectionStrings:Migration={database.MigrationConnectionString}"]));
        Assert.Equal(ExitCodes.Success, await RcsEntryPoint.RunAsync(["migrate", $"--ConnectionStrings:Migration={database.MigrationConnectionString}"]));
        Assert.Equal(ExitCodes.Success, await RcsEntryPoint.RunAsync(["check-schema", $"--ConnectionStrings:Runtime={database.RuntimeConnectionString}"]));
    }
}
