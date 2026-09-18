using Microsoft.Extensions.Options;
using Rcs.Infrastructure.Configuration;
using Rcs.Infrastructure.Documents;

namespace Rcs.Web.Documents;

/// <summary>
/// Collects interrupted uploads by age (DOCUMENT_MODEL.md §7.6) — the only file deletion the system performs, and it can
/// only see the temporary area. Published objects are never touched (ADR-043).
/// </summary>
public sealed class TemporaryUploadSweeper(LocalContentStore store, IOptions<StorageOptions> options, ILogger<TemporaryUploadSweeper> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromHours(6);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var retention = TimeSpan.FromHours(Math.Max(1, options.Value.TemporaryRetentionHours));
        using var timer = new PeriodicTimer(Interval);
        try
        {
            do
            {
                try
                {
                    var removed = store.SweepTemporaries(retention);
                    if (removed > 0)
                    {
                        logger.LogInformation("Removed {Count} interrupted temporary upload(s) older than {Hours} h.", removed, retention.TotalHours);
                    }
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    logger.LogWarning(exception, "The temporary upload sweep could not complete.");
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException)
        {
            // Host shutdown, including a startup that failed elsewhere: nothing to finish.
        }
    }
}
