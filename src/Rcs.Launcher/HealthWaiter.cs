namespace Rcs.Launcher;

/// <summary>Why the launcher stopped waiting for RCS.</summary>
public enum ReadyOutcome
{
    /// <summary><c>/health/ready</c> answered Healthy: the schema matches and the application will serve pages.</summary>
    Ready = 1,

    /// <summary>The application exited while we waited — a configuration or database problem, and its log says which.</summary>
    Stopped,

    /// <summary>Still not answering after the configured wait.</summary>
    TimedOut,
}

/// <summary>
/// Waits for the application to become usable rather than merely started. <c>/health/ready</c> is the right signal:
/// it reports Healthy only when the database is reachable AND its schema is exactly the one this release expects,
/// which is precisely the failure an employee cannot diagnose and must not be shown as a browser error page.
/// </summary>
public sealed class HealthWaiter(HttpClient client, TimeProvider? timeProvider = null)
{
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;

    /// <param name="isRunning">Whether the application process is still alive; null when the launcher did not start it.</param>
    public async Task<ReadyOutcome> WaitAsync(Uri health, TimeSpan timeout, Func<bool>? isRunning = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(health);
        var deadline = clock.GetUtcNow() + timeout;
        while (clock.GetUtcNow() < deadline)
        {
            if (isRunning is not null && !isRunning())
            {
                return ReadyOutcome.Stopped;
            }

            try
            {
                using var response = await client.GetAsync(health, cancellationToken);
                if (response.IsSuccessStatusCode)
                {
                    return ReadyOutcome.Ready;
                }
            }
            catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
            {
                // Not listening yet, or still starting. Both are normal for the first few seconds.
            }

            await Task.Delay(TimeSpan.FromMilliseconds(500), clock, cancellationToken);
        }

        return ReadyOutcome.TimedOut;
    }
}
