using Microsoft.Extensions.Options;
using Rcs.Infrastructure.Configuration;
using Rcs.Infrastructure.Migrations;

namespace Rcs.Web.Hosting;

/// <summary>
/// Startup gate. Runs before any hosted service starts (including the web server) and throws — so the host
/// fails to start — unless the schema matches this release exactly. It only reads; it never migrates.
/// </summary>
public sealed class SchemaCompatibilityGate(
    SchemaCompatibilityChecker checker,
    MigrationSet release,
    IOptions<DatabaseOptions> options,
    ILogger<SchemaCompatibilityGate> logger) : IHostedLifecycleService
{
    public async Task StartingAsync(CancellationToken cancellationToken)
    {
        ReleaseConfiguration.EnsureExpectedVersionMatchesBuild(options.Value, release);

        var report = await checker.CheckAsync(cancellationToken);
        if (!report.IsCompatible)
        {
            logger.LogCritical(
                "Refusing to start: database schema is {Status} (database version {DatabaseVersion}, expected {ExpectedVersion}). {Message}",
                report.Status, report.DatabaseVersion, report.ExpectedVersion, report.Message);
            throw new SchemaIncompatibleException(report);
        }

        logger.LogInformation("Database schema version {SchemaVersion} is compatible with this release.", report.DatabaseVersion);
    }

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StartedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StoppingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

internal static class ReleaseConfiguration
{
    /// <summary>
    /// <c>Rcs:Database:ExpectedSchemaVersion</c> is shipped with the release; it must agree with the migrations
    /// embedded in the same build, so the two can never drift apart silently.
    /// </summary>
    public static void EnsureExpectedVersionMatchesBuild(DatabaseOptions options, MigrationSet release)
    {
        if (options.ExpectedSchemaVersion != release.LatestVersion)
        {
            throw new InvalidOperationException(
                $"Release configuration mismatch: Rcs:Database:ExpectedSchemaVersion is {options.ExpectedSchemaVersion}, but this build embeds migrations up to {release.LatestVersion}.");
        }
    }
}
