using System.Diagnostics;
using System.Globalization;
using System.Text;
using Rcs.Application.Previews;
using Rcs.Domain.Documents;

namespace Rcs.PreviewWorker.Processing;

/// <summary>The exit and the (bounded) output of one converter run. Output is for classification only, never stored.</summary>
public sealed record ToolResult(int ExitCode, string StandardOutput, string StandardError);

/// <summary>
/// Starts one converter: no shell, an explicit argument list, a scrubbed environment, a wall-clock timeout that kills
/// the whole process tree, and — when <c>prlimit</c> is available — hard kernel limits on address space, CPU time,
/// file size and open files (SECURITY.md §11.5 constraint 3). Captured output is truncated; it is used to classify a
/// failure and is never written to the manifest verbatim.
/// </summary>
public static class ToolRunner
{
    private const int MaxCapturedChars = 16 * 1024;

    public static async Task<ToolResult> RunAsync(
        string tool,
        IReadOnlyList<string> arguments,
        PreviewWorkContext context,
        IReadOnlyDictionary<string, string>? environment = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentNullException.ThrowIfNull(context);
        return await RunAsync(tool, arguments, context.Tools, context.Limits, context.WorkDirectory, environment, cancellationToken);
    }

    public static async Task<ToolResult> RunAsync(
        string tool,
        IReadOnlyList<string> arguments,
        PreviewToolPaths tools,
        PreviewLimits limits,
        string workDirectory,
        IReadOnlyDictionary<string, string>? environment = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentNullException.ThrowIfNull(tools);
        ArgumentNullException.ThrowIfNull(limits);
        if (!Path.IsPathFullyQualified(tool) || !File.Exists(tool))
        {
            throw new PreviewFailureException(PreviewFailureCodes.ProcessorUnavailable, "A required converter is not installed.", unsupported: true);
        }

        var start = new ProcessStartInfo
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = workDirectory,
        };

        if (tools.Prlimit is { Length: > 0 } prlimit && Path.IsPathFullyQualified(prlimit) && File.Exists(prlimit))
        {
            start.FileName = prlimit;
            start.ArgumentList.Add(string.Create(CultureInfo.InvariantCulture, $"--as={limits.ToolMemoryBytes}"));
            start.ArgumentList.Add(string.Create(CultureInfo.InvariantCulture, $"--cpu={Math.Max(1, limits.ToolTimeoutSeconds)}"));
            start.ArgumentList.Add(string.Create(CultureInfo.InvariantCulture, $"--fsize={Math.Max(limits.MaxOutputBytes, limits.MaxInputBytes)}"));
            start.ArgumentList.Add("--nofile=512");
            start.ArgumentList.Add("--core=0");
            start.ArgumentList.Add("--");
            start.ArgumentList.Add(tool);
        }
        else
        {
            start.FileName = tool;
        }

        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        // Nothing is inherited: a converter sees a minimal, fixed environment and its own work directory as HOME.
        start.Environment.Clear();
        start.Environment["PATH"] = "/usr/bin:/bin";
        start.Environment["HOME"] = workDirectory;
        start.Environment["TMPDIR"] = workDirectory;
        start.Environment["LANG"] = "C.UTF-8";
        start.Environment["LC_ALL"] = "C.UTF-8";
        if (environment is not null)
        {
            foreach (var (name, value) in environment)
            {
                start.Environment[name] = value;
            }
        }

        using var process = new Process { StartInfo = start };
        try
        {
            process.Start();
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            throw new PreviewFailureException(PreviewFailureCodes.ProcessorUnavailable, "A required converter could not be started.", unsupported: true);
        }

        process.StandardInput.Close();
        var output = ReadBoundedAsync(process.StandardOutput);
        var error = ReadBoundedAsync(process.StandardError);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, limits.ToolTimeoutSeconds)));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            Kill(process);
            cancellationToken.ThrowIfCancellationRequested();
            throw new PreviewFailureException(PreviewFailureCodes.Timeout, "The converter did not finish within its time limit.");
        }

        return new ToolResult(process.ExitCode, await output, await error);
    }

    private static void Kill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
            process.WaitForExit(5000);
        }
        catch (InvalidOperationException)
        {
            // Already gone.
        }
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
