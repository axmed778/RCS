using System.Globalization;
using System.Text.RegularExpressions;
using Rcs.Application.Previews;
using Rcs.Domain.Documents;
using Rcs.PreviewWorker.Processing;

namespace Rcs.PreviewWorker.Processors;

/// <summary>
/// PNG / JPEG / WebP → a re-encoded preview image and a thumbnail, with libvips. The original bytes are never served as
/// a preview: the derivative is decoded and encoded again, EXIF-orientated, and stripped of every metadata block (EXIF,
/// GPS, XMP, ICC comments). The declared size is checked from the header before any decoder runs, and libvips is told
/// to refuse its untrusted loaders.
/// </summary>
public sealed partial class RasterImagePreviewProcessor : IDocumentPreviewProcessor
{
    private static readonly Dictionary<string, string> VipsEnvironment = new(StringComparer.Ordinal)
    {
        ["VIPS_BLOCK_UNTRUSTED"] = "1",
        ["VIPS_CONCURRENCY"] = "2",
        ["VIPS_DISC_THRESHOLD"] = "0",
    };

    public string ProcessorName => PreviewRouting.ImageProcessor;

    public string ImplementationVersion => "1";

    public bool CanProcess(string contentType) => PreviewRouting.Route(contentType).Processor == ProcessorName;

    public async Task<PreviewProcessorCapability> ProbeAsync(PreviewToolPaths tools, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tools);
        var version = await VipsVersionAsync(tools, cancellationToken);
        return version is null
            ? new(ProcessorName, ImplementationVersion, false, "libvips-tools (vips) is not installed")
            : new(ProcessorName, $"{ImplementationVersion};vips={version}", true, null);
    }

    public static async Task<string?> VipsVersionAsync(PreviewToolPaths tools, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tools);
        if (!PdfPageRenderer.Installed(tools.Vips))
        {
            return null;
        }

        var result = await ToolRunner.RunAsync(tools.Vips!, ["--version"], tools, new PreviewLimits { ToolTimeoutSeconds = 20 }, Path.GetTempPath(), VipsEnvironment, cancellationToken);
        var match = VersionPattern().Match(result.StandardOutput);
        return match.Success ? match.Groups[1].Value : null;
    }

    public async Task<ProcessorOutcome> GenerateAsync(PreviewWorkContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        context.RequireInputAtMost(context.Limits.MaxInputBytes);
        var declared = RasterHeader.Read(context.InputPath)
            ?? throw new PreviewFailureException(PreviewFailureCodes.Malformed, "The image header could not be read.");
        if (declared.Width > context.Limits.MaxImageDimension || declared.Height > context.Limits.MaxImageDimension
            || declared.Pixels > context.Limits.MaxImagePixels)
        {
            throw new PreviewFailureException(PreviewFailureCodes.ImageTooLarge, "The image dimensions exceed the preview limit.");
        }

        // Photographs stay JPEG; anything that may carry transparency (PNG, WebP) becomes PNG.
        var photo = string.Equals(context.ContentType, "image/jpeg", StringComparison.OrdinalIgnoreCase);
        var previewName = photo ? PreviewArtifactFiles.ImageJpeg : PreviewArtifactFiles.ImagePng;
        await ThumbnailAsync(context, context.InputPath, previewName, context.Limits.MaxPreviewPixels, photo ? "[Q=85,strip]" : "[strip]", cancellationToken);
        var size = RasterHeader.Read(context.OutputPath(previewName))
            ?? throw new PreviewFailureException(PreviewFailureCodes.ConversionFailed, "The preview image is not valid.");
        context.Record(previewName, PreviewArtifactKind.Image, photo ? "image/jpeg" : "image/png", width: size.Width, height: size.Height);

        await ThumbnailAsync(context, context.InputPath, PreviewArtifactFiles.Thumbnail, context.Limits.ThumbnailPixels, "[strip]", cancellationToken);
        var thumbnail = RasterHeader.Read(context.OutputPath(PreviewArtifactFiles.Thumbnail));
        context.Record(PreviewArtifactFiles.Thumbnail, PreviewArtifactKind.Thumbnail, "image/png", width: thumbnail?.Width, height: thumbnail?.Height);
        return new ProcessorOutcome(PreviewType.Image);
    }

    /// <summary>Shrinks (never enlarges) an image to fit a square of <paramref name="pixels"/> and writes it as an artifact.</summary>
    public static async Task ThumbnailAsync(PreviewWorkContext context, string source, string artifactName, int pixels, string saveOptions, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!PdfPageRenderer.Installed(context.Tools.Vips))
        {
            throw new PreviewFailureException(PreviewFailureCodes.ProcessorUnavailable, "libvips is not installed.", unsupported: true);
        }

        var result = await ToolRunner.RunAsync(context.Tools.Vips!,
        [
            "thumbnail", source, context.OutputPath(artifactName) + saveOptions,
            pixels.ToString(CultureInfo.InvariantCulture),
            "--height", pixels.ToString(CultureInfo.InvariantCulture),
            "--size", "down",
        ], context, VipsEnvironment, cancellationToken);
        if (result.ExitCode != 0 || !File.Exists(context.OutputPath(artifactName)))
        {
            throw new PreviewFailureException(PreviewFailureCodes.Malformed, "The image could not be decoded.");
        }
    }

    [GeneratedRegex(@"vips-([0-9][0-9.]*)")]
    private static partial Regex VersionPattern();
}
