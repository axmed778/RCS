using System.Data.Common;
using Rcs.Infrastructure.Configuration;
using Rcs.Infrastructure.Migrations;
using Rcs.Web.Hosting;

namespace Rcs.Web.Commands;

/// <summary><c>Rcs.Web migrate</c>: the explicit deployment action that applies pending migrations.</summary>
internal static class MigrateCommand
{
    public static async Task<int> RunAsync(string[] args)
    {
        var configuration = CommandConfiguration.Build(args);
        using var loggerFactory = CommandConfiguration.CreateLoggerFactory(configuration);
        var logger = loggerFactory.CreateLogger("Rcs.Migrate");

        var connectionString = configuration.GetConnectionString(ConnectionStringNames.Migration);
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            logger.LogError(
                "ConnectionStrings:Migration is not configured. Provide the rcs_migrate connection string to the deployment account through RCS_SECRETS_FILE or the ConnectionStrings__Migration environment variable. It must never be in the running service's configuration.");
            return ExitCodes.UsageOrConfigurationError;
        }

        var databaseOptions = configuration.GetSection(DatabaseOptions.SectionName).Get<DatabaseOptions>() ?? new DatabaseOptions();
        var runnerOptions = new MigrationRunnerOptions { LockTimeout = TimeSpan.FromSeconds(databaseOptions.MigrationLockTimeoutSeconds) };

        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler onCancel = (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        };
        Console.CancelKeyPress += onCancel;
        try
        {
            var release = MigrationSet.LoadEmbedded();
            ReleaseConfiguration.EnsureExpectedVersionMatchesBuild(databaseOptions, release);

            var runner = new MigrationRunner(loggerFactory.CreateLogger<MigrationRunner>());
            var result = await runner.RunAsync(connectionString, release, runnerOptions, cancellation.Token);
            logger.LogInformation(
                "Database schema is at version {SchemaVersion}; this run applied {AppliedCount} migration(s).",
                result.SchemaVersion, result.Applied.Count);
            return ExitCodes.Success;
        }
        catch (MigrationException exception)
        {
            logger.LogError(exception, "Migration stopped: {Reason}", exception.Message);
            return ExitCodes.Failure;
        }
        catch (DbException exception)
        {
            logger.LogError(exception, "Migration stopped: the database could not be reached or refused the connection.");
            return ExitCodes.Failure;
        }
        catch (InvalidOperationException exception)
        {
            logger.LogError("Migration stopped: {Reason}", exception.Message);
            return ExitCodes.UsageOrConfigurationError;
        }
        catch (OperationCanceledException)
        {
            logger.LogWarning("Migration cancelled. A migration interrupted inside its transaction was rolled back and not recorded.");
            return ExitCodes.Cancelled;
        }
        finally
        {
            Console.CancelKeyPress -= onCancel;
        }
    }
}
