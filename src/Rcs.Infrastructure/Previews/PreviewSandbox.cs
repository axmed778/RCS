using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Rcs.Infrastructure.Previews;

/// <summary>How one sandboxed worker run ended.</summary>
/// <param name="StandardOutput">Bounded; used by the capability probe. Never stored.</param>
/// <param name="StandardError">Bounded; logged for operators, never stored or shown to users.</param>
public sealed record SandboxRun(int ExitCode, bool TimedOut, string StandardOutput, string StandardError);

/// <summary>The directories one run may see: read-only ones and the ones it may write.</summary>
public sealed record SandboxMounts(IReadOnlyList<string> ReadOnly, IReadOnlyList<string> Writable);

/// <summary>
/// Starts the preview worker outside the application's trust boundary (SECURITY.md §11.5, ARCHITECTURE.md §14.4,
/// DECISIONS.md ADR-044). Whatever the mode, the worker never inherits the application's environment — so never its
/// connection strings or secrets-file path — and the whole process tree is killed at the job timeout.
/// </summary>
/// <remarks>
/// In <see cref="PreviewSandboxMode.Bubblewrap"/> mode the worker additionally gets: no network (a new, empty network
/// namespace — PostgreSQL is unreachable by TCP and its socket directory is not mounted), a new PID/IPC/UTS namespace,
/// a filesystem containing only <c>/usr</c>, a few named <c>/etc</c> entries, the .NET runtime and worker read-only, the
/// job's input read-only, and its output/work directories writable; <c>/home</c>, <c>/srv</c>, the object store and
/// the secrets file simply do not exist inside it.
/// </remarks>
public sealed class PreviewSandbox(IOptions<PreviewOptions> options, ILogger<PreviewSandbox> logger)
{
    private const int MaxCapturedChars = 64 * 1024;

    /// <summary>Configuration entries under /etc the converters need, and nothing else from /etc.</summary>
    private static readonly string[] EtcEntries = ["/etc/fonts", "/etc/ld.so.cache", "/etc/libreoffice", "/etc/alternatives", "/etc/localtime"];

    private readonly PreviewOptions settings = options.Value;

    public PreviewSandboxMode Mode => settings.Sandbox.Mode;

    /// <summary>The worker command, host first: <c>dotnet Rcs.PreviewWorker.dll</c> or the apphost.</summary>
    public IReadOnlyList<string> WorkerCommand()
    {
        var worker = Path.GetFullPath(string.IsNullOrWhiteSpace(settings.Worker.Path)
            ? Path.Combine(AppContext.BaseDirectory, "preview-worker", "Rcs.PreviewWorker.dll")
            : settings.Worker.Path);
        if (!worker.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
        {
            return [worker];
        }

        return [DotnetHost(), worker];
    }

    /// <summary>The worker, plus the runtime it needs, as read-only mounts.</summary>
    public IReadOnlyList<string> WorkerMounts()
    {
        var command = WorkerCommand();
        var mounts = new List<string> { Path.GetDirectoryName(command[^1])! };
        if (command.Count > 1)
        {
            mounts.Add(Path.GetDirectoryName(command[0])!);
        }

        return mounts;
    }

    public async Task<SandboxRun> RunAsync(IReadOnlyList<string> workerArguments, SandboxMounts mounts, string workDirectory, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(workerArguments);
        ArgumentNullException.ThrowIfNull(mounts);
        if (settings.Sandbox.Mode == PreviewSandboxMode.Unisolated
            && !string.Equals(Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT") ?? Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT"), "Development", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Unisolated preview execution is allowed only in Development, including CLI execution.");
        }

        var command = new List<string>(settings.Sandbox.LauncherPrefix.Where(part => part.Length > 0));
        var environment = WorkerEnvironment(workDirectory);

        if (settings.Sandbox.Mode == PreviewSandboxMode.Bubblewrap)
        {
            if (!OperatingSystem.IsLinux() || settings.Tools.Prlimit is not { Length: > 0 } limitTool || !File.Exists(limitTool))
            {
                throw new InvalidOperationException("The Linux preview sandbox requires prlimit.");
            }

            command.AddRange([limitTool, "--core=0", "--nofile=512",
                $"--cpu={Math.Max(5, settings.JobTimeoutSeconds)}",
                $"--fsize={Math.Max(settings.Limits.MaxOutputBytes, settings.Limits.MaxInputBytes)}", "--"]);
            command.AddRange(BubblewrapArguments(mounts, workDirectory, environment));
            environment = new Dictionary<string, string>(StringComparer.Ordinal) { ["PATH"] = "/usr/bin:/bin" };
        }

        command.AddRange(WorkerCommand());
        command.AddRange(workerArguments);

        var start = new ProcessStartInfo
        {
            FileName = command[0],
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = workDirectory,
        };
        foreach (var argument in command.Skip(1))
        {
            start.ArgumentList.Add(argument);
        }

        // Nothing of the application's environment crosses the boundary: no connection strings, no RCS_SECRETS_FILE.
        start.Environment.Clear();
        foreach (var (name, value) in environment)
        {
            start.Environment[name] = value;
        }

        using var process = new Process { StartInfo = start };
        try
        {
            process.Start();
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            logger.LogError("The preview sandbox could not be started ({Program}): {Error}", start.FileName, exception.GetType().Name);
            return new SandboxRun(-1, false, string.Empty, "sandbox start failed");
        }

        process.StandardInput.Close();
        var output = ReadBoundedAsync(process.StandardOutput);
        var error = ReadBoundedAsync(process.StandardError);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(5, settings.JobTimeoutSeconds)));
        var timedOut = false;
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            timedOut = !cancellationToken.IsCancellationRequested;
            try
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10), CancellationToken.None);
            }
            catch (Exception exception) when (exception is InvalidOperationException or TimeoutException)
            {
                logger.LogWarning("The preview sandbox did not exit cleanly after being killed.");
            }

            cancellationToken.ThrowIfCancellationRequested();
        }

        return new SandboxRun(timedOut ? -1 : process.ExitCode, timedOut, await output, await error);
    }

    private IEnumerable<string> BubblewrapArguments(SandboxMounts mounts, string workDirectory, IReadOnlyDictionary<string, string> environment)
    {
        yield return settings.Sandbox.BubblewrapPath;
        foreach (var flag in new[] { "--unshare-all", "--die-with-parent", "--new-session", "--clearenv" })
        {
            yield return flag;
        }

        foreach (var argument in new[]
                 {
                     "--ro-bind", "/usr", "/usr",
                     "--symlink", "usr/bin", "/bin",
                     "--symlink", "usr/lib", "/lib",
                     "--symlink", "usr/lib64", "/lib64",
                     "--symlink", "usr/sbin", "/sbin",
                     "--proc", "/proc",
                     "--dev", "/dev",
                     "--size", (Math.Max(16, settings.SandboxTempMegabytes) * 1024L * 1024L).ToString(CultureInfo.InvariantCulture),
                     "--tmpfs", "/tmp",
                 })
        {
            yield return argument;
        }

        foreach (var entry in EtcEntries)
        {
            yield return "--ro-bind-try";
            yield return entry;
            yield return entry;
        }

        foreach (var path in WorkerMounts().Concat(settings.Sandbox.ReadOnlyPaths).Concat(mounts.ReadOnly).Where(path => path.Length > 0).Distinct(StringComparer.Ordinal))
        {
            if (path.StartsWith("/usr/", StringComparison.Ordinal))
            {
                continue; // already visible
            }

            yield return "--ro-bind";
            yield return path;
            yield return path;
        }

        foreach (var path in mounts.Writable)
        {
            yield return "--bind";
            yield return path;
            yield return path;
        }

        foreach (var (name, value) in environment)
        {
            yield return "--setenv";
            yield return name;
            yield return value;
        }

        yield return "--chdir";
        yield return workDirectory;
        yield return "--";
    }

    private Dictionary<string, string> WorkerEnvironment(string workDirectory) => new(StringComparer.Ordinal)
    {
        ["PATH"] = "/usr/bin:/bin",
        ["HOME"] = workDirectory,
        ["TMPDIR"] = "/tmp",
        ["LANG"] = "C.UTF-8",
        ["LC_ALL"] = "C.UTF-8",
        ["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1",
        ["DOTNET_NOLOGO"] = "1",
        ["DOTNET_EnableDiagnostics"] = "0",
        ["DOTNET_gcServer"] = "0",
        ["DOTNET_GCHeapHardLimit"] = (Math.Max(64, settings.WorkerMemoryMegabytes) * 1024L * 1024L).ToString("X", CultureInfo.InvariantCulture),
    };

    private string DotnetHost()
    {
        if (!string.IsNullOrWhiteSpace(settings.Worker.DotnetHost))
        {
            return Path.GetFullPath(settings.Worker.DotnetHost);
        }

        if (Environment.ProcessPath is { } current && Path.GetFileNameWithoutExtension(current).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        {
            return current;
        }

        // <dotnet root>/shared/Microsoft.NETCore.App/<version>/ — the host lives three levels up.
        var runtime = RuntimeEnvironment.GetRuntimeDirectory();
        var root = Path.GetFullPath(Path.Combine(runtime, "..", "..", ".."));
        return Path.Combine(root, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");
    }

    private static async Task<string> ReadBoundedAsync(StreamReader reader)
    {
        var builder = new StringBuilder();
        var buffer = new char[4096];
        int read;
        while ((read = await reader.ReadAsync(buffer.AsMemory())) > 0)
        {
            if (builder.Length < MaxCapturedChars)
            {
                builder.Append(buffer, 0, Math.Min(read, MaxCapturedChars - builder.Length));
            }
        }

        return builder.ToString();
    }
}
