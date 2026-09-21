using Rcs.Domain.Vocabulary;

namespace Rcs.Domain.Documents;

/// <summary>
/// <c>document_preview.status</c> (DECISIONS.md ADR-044). A preview is derived cache: none of these states says
/// anything about the document version, which stays valid and downloadable whatever happens here.
/// </summary>
public enum PreviewStatus
{
    [DbCode("PENDING")] Pending = 1,
    [DbCode("PROCESSING")] Processing,
    [DbCode("READY")] Ready,
    [DbCode("FAILED")] Failed,

    /// <summary>No processor can render this format, or the processor it needs is not available on this server.</summary>
    [DbCode("UNSUPPORTED")] Unsupported,
}

/// <summary><c>document_preview.preview_type</c> — which viewer shows the artifacts.</summary>
public enum PreviewType
{
    /// <summary>Rendered page images: PDF, and Office documents converted to PDF first.</summary>
    [DbCode("PAGES")] Pages = 1,

    /// <summary>One re-encoded raster image and its thumbnail.</summary>
    [DbCode("IMAGE")] Image,

    /// <summary>A geometry document drawn by the RCS viewer itself: KMZ/KML placemarks, DXF drawings.</summary>
    [DbCode("GEOMETRY")] Geometry,

    /// <summary>Nothing to show: the format is unsupported.</summary>
    [DbCode("NONE")] None,
}

/// <summary><c>document_preview_artifact.artifact_kind</c>.</summary>
public enum PreviewArtifactKind
{
    [DbCode("PAGE")] Page = 1,
    [DbCode("IMAGE")] Image,
    [DbCode("THUMBNAIL")] Thumbnail,
    [DbCode("GEOMETRY")] Geometry,
}

/// <summary>
/// Sanitized failure codes. A code is stored instead of converter output, so nothing from inside the sandbox — a
/// path, a stack trace, a fragment of the document — reaches the database or a user.
/// </summary>
public static class PreviewFailureCodes
{
    /// <summary>No processor exists for the detected format (ArchiCAD, unclassified files, ...).</summary>
    public const string FormatNotSupported = "FORMAT_NOT_SUPPORTED";

    /// <summary>A processor is designed for the format but its converter is not installed or not approved (DWG).</summary>
    public const string ProcessorUnavailable = "PROCESSOR_UNAVAILABLE";

    /// <summary>The format could be rendered, but policy excludes it (macro-enabled Office — SECURITY.md §10.4).</summary>
    public const string ExcludedByPolicy = "EXCLUDED_BY_POLICY";

    public const string Timeout = "TIMEOUT";
    public const string ConversionFailed = "CONVERSION_FAILED";
    public const string WorkerFailed = "WORKER_FAILED";
    public const string InvalidOutput = "INVALID_OUTPUT";
    public const string SourceUnavailable = "SOURCE_UNAVAILABLE";
    public const string InputTooLarge = "INPUT_TOO_LARGE";
    public const string OutputTooLarge = "OUTPUT_TOO_LARGE";
    public const string ImageTooLarge = "IMAGE_TOO_LARGE";
    public const string Encrypted = "ENCRYPTED";
    public const string Malformed = "MALFORMED";
    public const string ArchiveUnsafe = "ARCHIVE_UNSAFE";
    public const string ArchiveTooLarge = "ARCHIVE_TOO_LARGE";
    public const string NoGeometry = "NO_GEOMETRY";

    /// <summary>Every code a worker may report. Anything else is replaced by <see cref="InvalidOutput"/>.</summary>
    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        FormatNotSupported, ProcessorUnavailable, ExcludedByPolicy, Timeout, ConversionFailed, WorkerFailed, InvalidOutput,
        SourceUnavailable, InputTooLarge, OutputTooLarge, ImageTooLarge, Encrypted, Malformed, ArchiveUnsafe,
        ArchiveTooLarge, NoGeometry,
    };

    /// <summary>
    /// Transient failures are retried automatically within the bounded attempt budget. A failure that depends only on
    /// the bytes (encrypted, malformed, too large, unsafe) would fail identically again, so it is final at once.
    /// </summary>
    public static bool IsTransient(string code) => code is Timeout or WorkerFailed or InvalidOutput or SourceUnavailable or ConversionFailed;
}

/// <summary>What the preview router decided for one stored version.</summary>
/// <param name="Processor">The processor that renders it, or the placeholder that honestly cannot.</param>
/// <param name="Type">The viewer the artifacts are for; <see cref="PreviewType.None"/> when nothing will be rendered.</param>
/// <param name="UnsupportedCode">Set when nothing will be rendered, with the reason.</param>
public sealed record PreviewRoute(string Processor, PreviewType Type, string? UnsupportedCode)
{
    public bool IsSupported => UnsupportedCode is null;
}

/// <summary>
/// The capability table of ADR-044: one processor per format family, chosen from the detected (stored) MIME type —
/// never from a filename. Routing is a pure decision; whether the processor is installed is a separate, probed fact.
/// </summary>
public static class PreviewRouting
{
    public const string PdfProcessor = "pdf-poppler";
    public const string ImageProcessor = "image-vips";
    public const string OfficeProcessor = "office-libreoffice";
    public const string KmzProcessor = "kmz-geometry";
    public const string DxfProcessor = "dxf-geometry";

    /// <summary>DWG has no approved local converter (ADR-044): the route exists so the capability is explicit.</summary>
    public const string DwgProcessor = "dwg-converter";

    /// <summary>ArchiCAD has no confirmed MIME mapping and no local converter (ADR-042, ADR-044).</summary>
    public const string ArchicadProcessor = "archicad-converter";

    /// <summary>Nothing is known about the bytes: stored, downloadable, never previewed.</summary>
    public const string NoProcessor = "none";

    public static readonly IReadOnlyList<string> AllProcessors =
        [PdfProcessor, ImageProcessor, OfficeProcessor, KmzProcessor, DxfProcessor, DwgProcessor, ArchicadProcessor];

    public static PreviewRoute Route(string mimeType, bool allowMacroEnabledOffice = false)
    {
        ArgumentNullException.ThrowIfNull(mimeType);
        var family = FileTypePolicy.FamilyOf(mimeType);
        return family switch
        {
            FileFamily.Pdf => new(PdfProcessor, PreviewType.Pages, null),
            FileFamily.Image => new(ImageProcessor, PreviewType.Image, null),

            // Macro-enabled documents are stored, marked and download-only (SECURITY.md §10.4): converting them would
            // not run the macros, but policy keeps them out of the pipeline unless an operator opts in.
            FileFamily.Word or FileFamily.Excel when FileTypePolicy.IsMacroEnabled(mimeType) && !allowMacroEnabledOffice =>
                new(OfficeProcessor, PreviewType.None, PreviewFailureCodes.ExcludedByPolicy),
            FileFamily.Word or FileFamily.Excel => new(OfficeProcessor, PreviewType.Pages, null),

            FileFamily.Kmz => new(KmzProcessor, PreviewType.Geometry, null),
            FileFamily.AutoCad when string.Equals(mimeType, "image/vnd.dxf", StringComparison.OrdinalIgnoreCase) =>
                new(DxfProcessor, PreviewType.Geometry, null),
            FileFamily.AutoCad => new(DwgProcessor, PreviewType.None, PreviewFailureCodes.ProcessorUnavailable),
            FileFamily.ArchiCad => new(ArchicadProcessor, PreviewType.None, PreviewFailureCodes.ProcessorUnavailable),
            _ => new(NoProcessor, PreviewType.None, PreviewFailureCodes.FormatNotSupported),
        };
    }
}
