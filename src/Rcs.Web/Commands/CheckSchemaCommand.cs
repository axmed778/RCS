using System.Data.Common;
using Npgsql;
using Rcs.Infrastructure.Configuration;
using Rcs.Infrastructure.Migrations;
using Rcs.Web.Hosting;

namespace Rcs.Web.Commands;

/// <summary>
/// <c>Rcs.Web check-schema</c>: runs the startup compatibility check with the runtime credentials, without
/// starting the web application. For deployment scripts: exit 0 when compatible, 3 when not.
/// </summary>
internal static class CheckSchemaCommand
{
    public static async Task<int> RunAsync(string[] args)
    {
        var configuration = CommandConfiguration.Build(args);
        using var loggerFactory = CommandConfiguration.CreateLoggerFactory(configuration);
        var logger = loggerFactory.CreateLogger("Rcs.CheckSchema");

        var connectionString = configuration.GetConnectionString(ConnectionStringNames.Runtime);
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            logger.LogError("ConnectionStrings:Runtime is not configured.");
            return ExitCodes.UsageOrConfigurationError;
        }

        try
        {
            var release = MigrationSet.LoadEmbedded();
            var databaseOptions = configuration.GetSection(DatabaseOptions.SectionName).Get<DatabaseOptions>() ?? new DatabaseOptions();
            ReleaseConfiguration.EnsureExpectedVersionMatchesBuild(databaseOptions, release);

            await using var dataSource = NpgsqlDataSource.Create(connectionString);
            var report = await new SchemaCompatibilityChecker(dataSource, release).CheckAsync();
            if (report.IsCompatible)
            {
                logger.LogInformation("{Status}: {Message}", report.Status, report.Message);
                return ExitCodes.Success;
            }

            logger.LogError("{Status}: {Message}", report.Status, report.Message);
            return ExitCodes.SchemaIncompatible;
        }
        catch (DbException exception)
        {
            logger.LogError(exception, "The database could not be reached with the runtime credentials.");
            return ExitCodes.Failure;
        }
        catch (InvalidOperationException exception)
        {
            logger.LogError("{Reason}", exception.Message);
            return ExitCodes.UsageOrConfigurationError;
        }
    }
}
