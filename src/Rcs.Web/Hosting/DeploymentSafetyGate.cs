using Microsoft.Extensions.Options;
using Rcs.Infrastructure.Configuration;
using Rcs.Infrastructure.Previews;
using Rcs.Web.Review;

namespace Rcs.Web.Hosting;

/// <summary>Thrown at startup when a non-Development deployment is configured in a way that would put real data at risk.</summary>
public sealed class UnsafeDeploymentException(string message) : Exception(message);

/// <summary>
/// Startup checks that only apply once the environment is not Development — a pilot or a production server. They exist
/// because the dangerous configurations are the ones that work: an application that happily stores real letters inside
/// its own release directory runs perfectly until the day it is replaced by the next release.
/// </summary>
/// <remarks>
/// Checked here: real business bytes live outside the application directory; the preview store and its staging area do
/// too; and the Development review actor is off (the composition root refuses it outright, this is the second lock).
/// </remarks>
public sealed class DeploymentSafetyGate(
    IHostEnvironment environment,
    IOptions<StorageOptions> storage,
    IOptions<PreviewOptions> previews,
    IOptions<ReviewOptions> review,
    ILogger<DeploymentSafetyGate> logger) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (environment.IsDevelopment())
        {
            return Task.CompletedTask;
        }

        if (review.Value.Enabled)
        {
            throw new UnsafeDeploymentException("Rcs:Review:Enabled must be false outside Development.");
        }

        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(environment.ContentRootPath));
        Require(storage.Value.RootPath, "Rcs:Storage:RootPath", root, required: true);
        Require(storage.Value.TempPath, "Rcs:Storage:TempPath", root, required: true);
        Require(previews.Value.StorageRoot, "Rcs:Preview:StorageRoot", root, required: previews.Value.Enabled);
        Require(previews.Value.TempRoot, "Rcs:Preview:TempRoot", root, required: previews.Value.Enabled);

        var paths = new[] { storage.Value.RootPath, storage.Value.TempPath,
            previews.Value.Enabled ? previews.Value.StorageRoot : null, previews.Value.Enabled ? previews.Value.TempRoot : null }
            .Where(path => !string.IsNullOrWhiteSpace(path)).Select(path => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path!))).ToArray();
        for (var i = 0; i < paths.Length; i++)
        {
            for (var j = i + 1; j < paths.Length; j++)
            {
                if (Inside(paths[i], paths[j]) || Inside(paths[j], paths[i]))
                    throw new UnsafeDeploymentException("Object, upload, preview and preview staging roots must be separate, non-overlapping directories.");
            }
        }

        logger.LogInformation("Deployment safety checks passed for the {Environment} environment.", environment.EnvironmentName);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private static bool Inside(string path, string parent) => path.Equals(parent, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)
        || path.StartsWith(parent + Path.DirectorySeparatorChar, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    /// <summary>A configured path that is absolute, present, and outside the application's own directory.</summary>
    private static void Require(string? path, string setting, string contentRoot, bool required)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            if (required)
            {
                throw new UnsafeDeploymentException($"{setting} must be configured outside Development.");
            }

            return;
        }

        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (!Path.IsPathFullyQualified(path))
        {
            throw new UnsafeDeploymentException($"{setting} must be an absolute path outside Development (got a path relative to the application directory).");
        }

        if (Inside(full, contentRoot) || Inside(full, Path.TrimEndingDirectorySeparator(Path.GetFullPath(AppContext.BaseDirectory))))
        {
            throw new UnsafeDeploymentException(
                $"{setting} points inside the application directory ({full}). Business documents and derived previews must live outside the release, so deploying a new version cannot touch them.");
        }
    }
}
