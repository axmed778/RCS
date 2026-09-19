using Microsoft.Extensions.Options;
using Rcs.Application.Previews;
using Rcs.Infrastructure.Previews;

namespace Rcs.Web.Previews;

public sealed class PreviewService(IPreviewJobRunner runner, PreviewPaths paths, IOptions<PreviewOptions> options,
    IHostEnvironment environment, ILogger<PreviewService> logger) : BackgroundService
{
    public override Task StartAsync(CancellationToken cancellationToken)
    {
        var settings = options.Value;
        if (settings.Enabled && (!paths.IsConfigured || (!environment.IsDevelopment() && settings.Sandbox.Mode != PreviewSandboxMode.Bubblewrap)))
        {
            throw new InvalidOperationException("Preview requires separate storage roots and a production Linux sandbox.");
        }

        return base.StartAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.Enabled)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await runner.ReconcileAsync(stoppingToken);
                await Task.WhenAll(Enumerable.Range(0, options.Value.WorkerConcurrency).Select(_ => runner.RunNextAsync(stoppingToken)));
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Preview processing failed; durable jobs remain available for recovery.");
            }

            await Task.Delay(TimeSpan.FromSeconds(Math.Max(1, options.Value.PollIntervalSeconds)), stoppingToken);
        }
    }
}
