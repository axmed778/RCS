using System.Text.Json;
using System.Text.Json.Serialization;

namespace Rcs.Application.Previews;

/// <summary>
/// The contract between the application and the preview worker (DECISIONS.md ADR-044). The worker is a separate,
/// unprivileged process: it receives one job file naming a read-only input and an output directory, and answers with a
/// manifest file. It has no database credentials, no access to the content store and no network. Everything it writes
/// is re-validated by the application before a byte of it is stored.
/// </summary>
public static class PreviewProtocol
{
    /// <summary>Written by the worker into the output directory; the only file the application reads by name.</summary>
    public const string ManifestFileName = "manifest.json";

    /// <summary>The largest manifest the application will parse.</summary>
    public const int MaxManifestBytes = 1024 * 1024;

    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        MaxDepth = 16,
        WriteIndented = false,
    };
}

/// <summary>Resource limits for one job. The worker enforces them; the application re-checks the result independently.</summary>
public sealed record PreviewLimits
{
    /// <summary>Largest input a processor will open. The 500 MB upload limit does not imply a 500 MB conversion.</summary>
    public long MaxInputBytes { get; init; } = 200L * 1024 * 1024;

    /// <summary>Pages rendered for a paged preview; later pages are counted but not rendered.</summary>
    public int MaxPages { get; init; } = 150;

    /// <summary>Rendering resolution of a page, before the pixel cap below.</summary>
    public int RenderDpi { get; init; } = 110;

    /// <summary>The long side of a rendered page or preview image, in pixels.</summary>
    public int MaxPreviewPixels { get; init; } = 1800;

    /// <summary>The long side of a thumbnail, in pixels.</summary>
    public int ThumbnailPixels { get; init; } = 320;

    /// <summary>A source raster larger than this (width × height) is refused before it is decoded.</summary>
    public long MaxImagePixels { get; init; } = 80_000_000;

    /// <summary>Either side of a source raster larger than this is refused before it is decoded.</summary>
    public int MaxImageDimension { get; init; } = 20_000;

    public long MaxArtifactBytes { get; init; } = 32L * 1024 * 1024;

    public long MaxOutputBytes { get; init; } = 256L * 1024 * 1024;

    public int KmzMaxEntries { get; init; } = 2_000;

    /// <summary>Total declared uncompressed size of a KMZ; the KML itself is also counted while it is read.</summary>
    public long KmzMaxUncompressedBytes { get; init; } = 100L * 1024 * 1024;

    public long KmlMaxBytes { get; init; } = 50L * 1024 * 1024;

    /// <summary>Compression ratio above which an entry is treated as a decompression bomb.</summary>
    public int KmzMaxCompressionRatio { get; init; } = 200;

    public int MaxFeatures { get; init; } = 20_000;

    public int MaxCoordinates { get; init; } = 1_000_000;

    /// <summary>Address-space limit applied to each converter process the worker starts (prlimit RLIMIT_AS).</summary>
    public long ToolMemoryBytes { get; init; } = 4L * 1024 * 1024 * 1024;

    /// <summary>Wall-clock budget for one converter invocation inside the worker.</summary>
    public int ToolTimeoutSeconds { get; init; } = 150;
}

/// <summary>Absolute paths of the converters inside the sandbox. Empty means "not installed".</summary>
public sealed record PreviewToolPaths
{
    public string? Soffice { get; init; } = "/usr/bin/soffice";

    public string? Pdftoppm { get; init; } = "/usr/bin/pdftoppm";

    public string? Pdfinfo { get; init; } = "/usr/bin/pdfinfo";

    public string? Vips { get; init; } = "/usr/bin/vips";

    public string? Prlimit { get; init; } = "/usr/bin/prlimit";
}

/// <summary>The job file handed to the worker.</summary>
/// <param name="Processor">A <c>PreviewRouting</c> processor name.</param>
/// <param name="InputPath">The staged, read-only copy of the version's bytes. Its name carries only the canonical extension.</param>
/// <param name="InputContentType">The stored (detected) MIME type — the worker never guesses a type from a name.</param>
/// <param name="OutputDirectory">Where artifacts and the manifest go. The only place the worker may write, besides its work directory.</param>
/// <param name="WorkDirectory">Scratch space for converters (LibreOffice profile, intermediate PDF).</param>
public sealed record PreviewJobRequest(
    string Processor,
    string InputPath,
    string InputContentType,
    string OutputDirectory,
    string WorkDirectory,
    PreviewLimits Limits,
    PreviewToolPaths Tools);

/// <summary>One file the worker produced, named relative to the output directory.</summary>
public sealed record PreviewManifestArtifact(
    string File,
    string Kind,
    string ContentType,
    int? PageNumber,
    int? Width,
    int? Height);

/// <summary>The worker's answer.</summary>
/// <param name="Status"><c>READY</c>, <c>FAILED</c> or <c>UNSUPPORTED</c>.</param>
/// <param name="FailureCode">A <c>PreviewFailureCodes</c> value; anything else is treated as invalid output.</param>
/// <param name="FailureMessage">Short, sanitized; the application sanitizes it again and never stores raw converter output.</param>
public sealed record PreviewManifest(
    string Status,
    string? FailureCode,
    string? FailureMessage,
    string? PreviewType,
    int? PageCount,
    int? PagesRendered,
    IReadOnlyList<PreviewManifestArtifact> Artifacts);

/// <summary>What one processor can do on this server, as probed inside the sandbox.</summary>
/// <param name="Version">The processor implementation and the converter versions it depends on, e.g. <c>1;poppler=24.02.0</c>.</param>
/// <param name="Reason">Why it is unavailable (a missing converter), in plain words for operators.</param>
public sealed record PreviewProcessorCapability(string Processor, string Version, bool Available, string? Reason);

/// <summary>The worker's <c>probe</c> answer.</summary>
public sealed record PreviewCapabilities(IReadOnlyList<PreviewProcessorCapability> Processors);

/// <summary>
/// The fixed artifact file names a worker may produce. The application accepts exactly these and nothing else, so no
/// name chosen by a converter, a document or an attacker ever reaches the preview store.
/// </summary>
public static partial class PreviewArtifactFiles
{
    public const string Thumbnail = "thumbnail.png";
    public const string ImagePng = "image.png";
    public const string ImageJpeg = "image.jpg";
    public const string Geometry = "geometry.json";

    public static string Page(int number) => string.Create(System.Globalization.CultureInfo.InvariantCulture, $"page-{number:D4}.png");

    public static bool IsValid(string? fileName) => fileName is not null && NamePattern().IsMatch(fileName);

    public static string Require(string fileName) =>
        IsValid(fileName) ? fileName : throw new ArgumentException("Not a preview artifact name.", nameof(fileName));

    [System.Text.RegularExpressions.GeneratedRegex(@"^(page-[0-9]{4}\.png|thumbnail\.png|image\.png|image\.jpg|geometry\.json)\z")]
    private static partial System.Text.RegularExpressions.Regex NamePattern();
}
