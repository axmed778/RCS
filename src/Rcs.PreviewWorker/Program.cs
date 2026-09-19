using System.Text.Json;
using Rcs.Application.Previews;
using Rcs.Domain.Documents;
using Rcs.Domain.Vocabulary;
using Rcs.PreviewWorker.Processing;
using Rcs.PreviewWorker.Processors;

namespace Rcs.PreviewWorker;

/// <summary>
/// <c>Rcs.PreviewWorker run &lt;job.json&gt;</c> renders one job and writes its manifest;
/// <c>Rcs.PreviewWorker probe &lt;tools.json&gt;</c> reports which processors can run here. The worker holds no
/// secrets and opens no connection: its whole world is the job file, the read-only input and its output directory.
/// </summary>
public static class Program
{
    public const int Success = 0;
    public const int Usage = 2;
    public const int Crashed = 3;

    private const int MaxJobFileBytes = 64 * 1024;

    public static async Task<int> Main(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        if (args.Length != 2)
        {
            await Console.Error.WriteLineAsync("usage: Rcs.PreviewWorker run <job.json> | probe <tools.json>");
            return Usage;
        }

        try
        {
            switch (args[0])
            {
                case "run":
                    var job = await ReadAsync<PreviewJobRequest>(args[1]);
                    await RunAsync(job, CancellationToken.None);
                    return Success;
                case "probe":
                    var tools = await ReadAsync<PreviewToolPaths>(args[1]);
                    var capabilities = await ProbeAsync(tools, CancellationToken.None);
                    await Console.Out.WriteLineAsync(JsonSerializer.Serialize(capabilities, PreviewProtocol.Json));
                    return Success;
                default:
                    await Console.Error.WriteLineAsync("unknown command");
                    return Usage;
            }
        }
        catch (Exception exception) when (exception is JsonException or IOException or ArgumentException or UnauthorizedAccessException)
        {
            // The type only: a message could quote a path or document content, and stderr is logged by the application.
            await Console.Error.WriteLineAsync($"worker error: {exception.GetType().Name}");
            return Crashed;
        }
    }

    public static async Task<PreviewCapabilities> ProbeAsync(PreviewToolPaths tools, CancellationToken cancellationToken)
    {
        var results = new List<PreviewProcessorCapability>();
        foreach (var processor in PreviewProcessors.All)
        {
            try
            {
                results.Add(await processor.ProbeAsync(tools, cancellationToken));
            }
            catch (PreviewFailureException exception)
            {
                results.Add(new PreviewProcessorCapability(processor.ProcessorName, processor.ImplementationVersion, false, exception.Message));
            }
        }

        return new PreviewCapabilities(results);
    }

    /// <summary>Runs one job and writes <c>manifest.json</c>. Every outcome — success or classified failure — is a manifest.</summary>
    public static async Task<PreviewManifest> RunAsync(PreviewJobRequest job, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);
        Validate(job);
        var context = new PreviewWorkContext(job);
        PreviewManifest manifest;
        var processor = PreviewProcessors.Find(job.Processor);
        try
        {
            if (processor is null || !processor.CanProcess(job.InputContentType))
            {
                throw new PreviewFailureException(PreviewFailureCodes.FormatNotSupported, "No processor handles this format.", unsupported: true);
            }

            var outcome = await processor.GenerateAsync(context, cancellationToken);
            manifest = new PreviewManifest(
                PreviewStatus.Ready.ToCode(), null, null, outcome.Type.ToCode(), outcome.PageCount, outcome.PagesRendered, context.Artifacts);
        }
        catch (PreviewFailureException failure)
        {
            manifest = new PreviewManifest(
                failure.Unsupported ? PreviewStatus.Unsupported.ToCode() : PreviewStatus.Failed.ToCode(),
                failure.Code, failure.Message, null, null, null, []);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException
                                              or FormatException or OverflowException or NotSupportedException or ArgumentException or OutOfMemoryException)
        {
            await Console.Error.WriteLineAsync($"processor error: {exception.GetType().Name}");
            manifest = new PreviewManifest(PreviewStatus.Failed.ToCode(), PreviewFailureCodes.ConversionFailed, "The processor failed on this file.", null, null, null, []);
        }

        await using var stream = new FileStream(Path.Combine(job.OutputDirectory, PreviewProtocol.ManifestFileName), FileMode.Create, FileAccess.Write);
        await JsonSerializer.SerializeAsync(stream, manifest, PreviewProtocol.Json, cancellationToken);
        return manifest;
    }

    private static void Validate(PreviewJobRequest job)
    {
        foreach (var path in new[] { job.InputPath, job.OutputDirectory, job.WorkDirectory })
        {
            if (!Path.IsPathFullyQualified(path))
            {
                throw new ArgumentException("Job paths must be absolute.", nameof(job));
            }
        }

        if (!File.Exists(job.InputPath) || !Directory.Exists(job.OutputDirectory) || !Directory.Exists(job.WorkDirectory))
        {
            throw new ArgumentException("The job's input or directories do not exist.", nameof(job));
        }
    }

    private static async Task<T> ReadAsync<T>(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.Length > MaxJobFileBytes)
        {
            throw new ArgumentException("The job file is missing or too large.", nameof(path));
        }

        await using var stream = info.OpenRead();
        return await JsonSerializer.DeserializeAsync<T>(stream, PreviewProtocol.Json)
            ?? throw new JsonException("Empty job file.");
    }
}
