using System.Data.Common;
using Microsoft.Extensions.DependencyInjection;
using Rcs.Infrastructure;
using Rcs.Infrastructure.Configuration;
using Rcs.Infrastructure.Development;
using Rcs.Infrastructure.Migrations;
using Rcs.Web.Hosting;

namespace Rcs.Web.Commands;

/// <summary>
/// <c>Rcs.Web seed-demo</c>: loads the Development-only synthetic demonstration data. It refuses to run in any other
/// environment, and it is never invoked by application startup — it is an explicit action (SECURITY.md §18.4).
/// Re-running it is safe: existing demo cases are left untouched.
/// </summary>
internal static class SeedDemoCommand
{
    public static async Task<int> RunAsync(string[] args)
    {
        var configuration = CommandConfiguration.Build(args);
        using var loggerFactory = CommandConfiguration.CreateLoggerFactory(configuration);
        var logger = loggerFactory.CreateLogger("Rcs.SeedDemo");

        var environment = Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT")
            ?? Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT")
            ?? Environments.Production;
        if (!string.Equals(environment, Environments.Development, StringComparison.OrdinalIgnoreCase))
        {
            logger.LogError(
                "The demo seed is Development-only synthetic data. The current environment is '{Environment}'. Set DOTNET_ENVIRONMENT=Development on a development machine, never on a production server.",
                environment);
            return ExitCodes.UsageOrConfigurationError;
        }

        var services = new ServiceCollection();
        services.AddLogging(logging => logging.AddConfiguration(configuration.GetSection("Logging")).AddSimpleConsole(console => console.SingleLine = true));
        services.AddRcsInfrastructure(configuration);

        await using var provider = services.BuildServiceProvider();
        try
        {
            // The schema must already be the one this release expects; the seed never migrates.
            var report = await provider.GetRequiredService<SchemaCompatibilityChecker>().CheckAsync();
            if (!report.IsCompatible)
            {
                logger.LogError("{Status}: {Message}", report.Status, report.Message);
                return ExitCodes.SchemaIncompatible;
            }

            var seeded = await provider.GetRequiredService<DemoDataSeeder>().SeedAsync(isDevelopmentEnvironment: true);
            logger.LogInformation(
                seeded
                    ? "Demo data is ready. Start the application and open the case list."
                    : "Demo data was already present; nothing was created.");
            return ExitCodes.Success;
        }
        catch (DbException exception)
        {
            logger.LogError(exception, "The database could not be reached with the runtime credentials.");
            return ExitCodes.Failure;
        }
        catch (InvalidOperationException exception)
        {
            logger.LogError("Seeding stopped: {Reason}", exception.Message);
            return ExitCodes.Failure;
        }
    }
}
