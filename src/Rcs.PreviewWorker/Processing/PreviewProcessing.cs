using Rcs.Application.Previews;
using Rcs.Domain.Documents;
using Rcs.Domain.Vocabulary;

namespace Rcs.PreviewWorker.Processing;

/// <summary>
/// One format family's preview processor (DECISIONS.md ADR-044). A processor runs only inside the worker process, only
/// against a staged read-only input, and writes only into the job's output and work directories.
/// </summary>
public interface IDocumentPreviewProcessor
{
    /// <summary>The routing name (<c>PreviewRouting</c>), recorded on every generation it produces.</summary>
    string ProcessorName { get; }

    /// <summary>The implementation's own version. Bumped whenever its output would change.</summary>
    string ImplementationVersion { get; }

    bool CanProcess(string contentType);

    /// <summary>Whether the converters this processor needs exist here, and their versions.</summary>
    Task<PreviewProcessorCapability> ProbeAsync(PreviewToolPaths tools, CancellationToken cancellationToken);

    /// <summary>Renders the input. Failures are <see cref="PreviewFailureException"/>; artifacts go through <paramref name="context"/>.</summary>
    Task<ProcessorOutcome> GenerateAsync(PreviewWorkContext context, CancellationToken cancellationToken);
}

/// <summary>What a successful run produced, besides its artifacts.</summary>
public sealed record ProcessorOutcome(PreviewType Type, int? PageCount = null, int? PagesRendered = null);

/// <summary>An expected, classified failure. The message is short and must not contain paths or document content.</summary>
public sealed class PreviewFailureException : Exception
{
    public PreviewFailureException(string code, string message, bool unsupported = false)
        : base(message)
    {
        Code = code;
        Unsupported = unsupported;
    }

    public PreviewFailureException()
    {
        Code = PreviewFailureCodes.WorkerFailed;
    }

    public PreviewFailureException(string message)
        : base(message)
    {
        Code = PreviewFailureCodes.WorkerFailed;
    }

    public PreviewFailureException(string message, Exception innerException)
        : base(message, innerException)
    {
        Code = PreviewFailureCodes.WorkerFailed;
    }

    public string Code { get; }

    /// <summary>The generation ends UNSUPPORTED rather than FAILED: nothing about the bytes is wrong, the format just isn't renderable.</summary>
    public bool Unsupported { get; }
}

/// <summary>
/// The job as a processor sees it: the input, the limits, the tools, and the only way to publish an output file. Every
/// artifact is checked against the limits as it is recorded, so a runaway converter is stopped by the worker before
/// the application ever sees its output.
/// </summary>
public sealed class PreviewWorkContext
{
    private readonly List<PreviewManifestArtifact> artifacts = [];
    private long outputBytes;

    public PreviewWorkContext(PreviewJobRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        Request = request;
    }

    public PreviewJobRequest Request { get; }

    public string InputPath => Request.InputPath;

    public string ContentType => Request.InputContentType;

    public PreviewLimits Limits => Request.Limits;

    public PreviewToolPaths Tools => Request.Tools;

    public string OutputDirectory => Request.OutputDirectory;

    public string WorkDirectory => Request.WorkDirectory;

    public IReadOnlyList<PreviewManifestArtifact> Artifacts => artifacts;

    public long InputBytes => new FileInfo(InputPath).Length;

    /// <summary>The full path of an output file. Only fixed, code-chosen names are ever used.</summary>
    public string OutputPath(string fileName) => Path.Combine(OutputDirectory, PreviewArtifactFiles.Require(fileName));

    public void RequireInputAtMost(long maxBytes)
    {
        if (InputBytes > maxBytes)
        {
            throw new PreviewFailureException(PreviewFailureCodes.InputTooLarge, "The file is larger than the preview limit for its format.");
        }
    }

    /// <summary>Records a file already written to the output directory, after checking its size against the limits.</summary>
    public void Record(string fileName, PreviewArtifactKind kind, string contentType, int? pageNumber = null, int? width = null, int? height = null)
    {
        var info = new FileInfo(OutputPath(fileName));
        if (!info.Exists || info.Length == 0)
        {
            throw new PreviewFailureException(PreviewFailureCodes.ConversionFailed, "The converter produced no output.");
        }

        if (info.Length > Limits.MaxArtifactBytes || (outputBytes += info.Length) > Limits.MaxOutputBytes)
        {
            throw new PreviewFailureException(PreviewFailureCodes.OutputTooLarge, "The preview output exceeds the configured limit.");
        }

        artifacts.Add(new PreviewManifestArtifact(fileName, kind.ToCode(), contentType, pageNumber, width, height));
    }
}
