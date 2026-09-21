namespace Rcs.Launcher;

/// <summary>
/// Keeps one RCS running per laptop. The second double-click must not start a second application — two processes
/// would fight over the same port, the same document store and the same preview staging area. Instead the second
/// launch finds the first, opens the browser again and exits.
/// </summary>
/// <remarks>
/// A named mutex is the mechanism because the operating system releases it when the process dies, however it dies.
/// A lock file would survive a power cut and leave the employee with an application that refuses to start and a
/// file they have to know about and delete.
/// </remarks>
public sealed class SingleInstance : IDisposable
{
    public const string DefaultName = "Global\\RCS.Pilot.Launcher";

    private Mutex? mutex;

    private SingleInstance(Mutex mutex) => this.mutex = mutex;

    /// <summary>True when this process now owns the instance; false when RCS is already running.</summary>
    public static bool TryAcquire(string name, out SingleInstance? instance)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        instance = null;
        Mutex? candidate = null;
        try
        {
            candidate = new Mutex(initiallyOwned: false, name);
            // A zero timeout: either it is free now, or another launcher owns it.
            if (!candidate.WaitOne(TimeSpan.Zero, exitContext: false))
            {
                candidate.Dispose();
                return false;
            }
        }
        catch (AbandonedMutexException)
        {
            // The previous owner died without releasing it; this process is now the owner, which is what we want.
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException or NotSupportedException)
        {
            // A platform or policy that will not give us a named mutex must not stop the employee from working.
            candidate?.Dispose();
            instance = null;
            return true;
        }

        instance = new SingleInstance(candidate!);
        return true;
    }

    public void Dispose()
    {
        if (mutex is null)
        {
            return;
        }

        try
        {
            mutex.ReleaseMutex();
        }
        catch (Exception exception) when (exception is ApplicationException or ObjectDisposedException)
        {
            // Already released, or never owned. Nothing to do.
        }

        mutex.Dispose();
        mutex = null;
    }
}
