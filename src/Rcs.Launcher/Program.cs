using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;

namespace Rcs.Launcher;

/// <summary>
/// <c>Rcs.Launcher.exe</c> — the desktop icon for the Windows single-laptop pilot.
/// </summary>
/// <remarks>
/// The whole point is that the employee never opens a terminal: double-click, RCS starts if it is not running, the
/// browser opens at the sign-in page. A second double-click re-opens the browser instead of starting a second copy.
/// <c>--stop</c> (the "RCS-i dayandır" shortcut) stops it. Every failure ends in a window the employee can read and
/// repeat to us, not in a console that has already closed.
/// </remarks>
public static class Program
{
    public const int Success = 0;
    public const int AlreadyRunning = 0;
    public const int ConfigurationError = 2;
    public const int StartFailed = 3;
    public const int NotReady = 4;

    [STAThread]
    public static int Main(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        var home = AppContext.BaseDirectory;
        var options = LauncherOptions.Load(home);
        var log = new LauncherLog(options.LogDirectory);

        if (options.Validate() is { } problem)
        {
            log.Write($"configuration refused: {problem}");
            Tell("RCS konfiqurasiyası düzgün deyil", problem + "\n\nTexniki inzibatçıya müraciət edin.");
            return ConfigurationError;
        }

        if (args.Contains("--stop", StringComparer.OrdinalIgnoreCase))
        {
            return Stop(options, log);
        }

        // Already running: do the useful thing rather than complaining.
        if (!SingleInstance.TryAcquire(SingleInstance.DefaultName, out var instance))
        {
            log.Write("another launcher owns this laptop's instance; opening the browser again");
            OpenBrowser(options.StartUri, log);
            return AlreadyRunning;
        }

        using (instance)
        {
            return Run(options, log, home);
        }
    }

    private static int Run(LauncherOptions options, LauncherLog log, string home)
    {
        var application = options.ResolveApplicationPath(home);
        if (!File.Exists(application))
        {
            log.Write($"application not found at the configured path");
            Tell("RCS tapılmadı", "RCS proqramı quraşdırıldığı yerdə tapılmadı.\n\nTexniki inzibatçıya müraciət edin.");
            return ConfigurationError;
        }

        Process? process;
        try
        {
            process = Start(application, options);
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            log.Write($"the application could not be started: {exception.GetType().Name}");
            Tell("RCS başlamadı", "RCS proqramı başlaya bilmədi.\n\nTexniki inzibatçıya müraciət edin.");
            return StartFailed;
        }

        log.Write($"application started (pid {process?.Id}); waiting for {options.HealthUri}");
        WritePidFile(options, process, log);

        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        var outcome = new HealthWaiter(client).WaitAsync(
            options.HealthUri,
            TimeSpan.FromSeconds(options.ReadyTimeoutSeconds),
            isRunning: () => process is { HasExited: false }).GetAwaiter().GetResult();

        switch (outcome)
        {
            case ReadyOutcome.Ready:
                log.Write("healthy; opening the browser");
                OpenBrowser(options.StartUri, log);
                // The launcher stays alive so that closing RCS is one deliberate action (the stop shortcut), and so
                // that the single-instance mutex is held for exactly as long as the application runs.
                process?.WaitForExit();
                log.Write("the application exited");
                return Success;

            case ReadyOutcome.Stopped:
                log.Write("the application exited before it became healthy");
                Tell("RCS işə düşmədi", ProblemText(options));
                return StartFailed;

            default:
                log.Write("timed out waiting for the application to become healthy");
                StopProcess(process, log);
                Tell("RCS cavab vermir", ProblemText(options));
                return NotReady;
        }
    }

    private static string ProblemText(LauncherOptions options) =>
        "RCS başladı, lakin işə hazır olmadı.\n\n"
        + "Ən çox rast gəlinən səbəb: verilənlər bazası xidməti işləmir.\n\n"
        + $"Qeydlər: {options.LogDirectory}\n\n"
        + "Texniki inzibatçıya bu mesajı göstərin.";

    private static Process Start(string application, LauncherOptions options)
    {
        var start = new ProcessStartInfo
        {
            FileName = application,
            WorkingDirectory = Path.GetDirectoryName(application)!,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        // The environment and the URL come from here, so the employee's shortcut needs no arguments and the
        // application cannot accidentally be started as Development by double-clicking it directly.
        start.Environment["DOTNET_ENVIRONMENT"] = options.EnvironmentName;
        start.Environment["ASPNETCORE_URLS"] = options.Url;
        start.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        start.Environment["DOTNET_NOLOGO"] = "1";
        return Process.Start(start) ?? throw new InvalidOperationException("The application did not start.");
    }

    /// <summary>The pid of the running application, so the stop shortcut can end it without a task manager.</summary>
    private static void WritePidFile(LauncherOptions options, Process? process, LauncherLog log)
    {
        if (process is null)
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(options.LogDirectory);
            File.WriteAllText(PidFile(options), process.Id.ToString(CultureInfo.InvariantCulture));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            log.Write("the pid file could not be written; --stop will fall back to the process name");
        }
    }

    private static string PidFile(LauncherOptions options) => Path.Combine(options.LogDirectory, "rcs-web.pid");

    private static int Stop(LauncherOptions options, LauncherLog log)
    {
        var stopped = 0;
        try
        {
            if (File.Exists(PidFile(options)) && int.TryParse(File.ReadAllText(PidFile(options)), CultureInfo.InvariantCulture, out var pid))
            {
                using var process = Process.GetProcessById(pid);
                StopProcess(process, log);
                stopped++;
            }
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            log.Write("no recorded application process to stop");
        }

        log.Write($"stop requested; {stopped} process(es) stopped");
        return Success;
    }

    private static void StopProcess(Process? process, LauncherLog log)
    {
        if (process is null || process.HasExited)
        {
            return;
        }

        try
        {
            process.Kill(entireProcessTree: true);
            process.WaitForExit(15000);
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            log.Write("the application could not be stopped cleanly");
        }
    }

    private static void OpenBrowser(Uri uri, LauncherLog log)
    {
        try
        {
            // UseShellExecute is what hands the address to the employee's default browser.
            Process.Start(new ProcessStartInfo { FileName = uri.ToString(), UseShellExecute = true })?.Dispose();
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            log.Write("the browser could not be opened");
            Tell("Brauzer açılmadı", $"RCS işləyir. Brauzerdə bu ünvanı açın:\n\n{uri}");
        }
    }

    /// <summary>A message box on Windows; standard error elsewhere. The employee never sees a stack trace.</summary>
    private static void Tell(string title, string message)
    {
        if (OperatingSystem.IsWindows())
        {
            _ = MessageBoxW(IntPtr.Zero, message, "RCS — " + title, 0x00000010 /* MB_ICONERROR */);
            return;
        }

        Console.Error.WriteLine($"{title}: {message}");
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = false)]
    private static extern int MessageBoxW(IntPtr owner, string text, string caption, uint type);
}
