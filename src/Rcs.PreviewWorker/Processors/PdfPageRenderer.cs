using System.Globalization;
using System.Text.RegularExpressions;
using Rcs.Application.Previews;
using Rcs.Domain.Documents;
using Rcs.PreviewWorker.Processing;

namespace Rcs.PreviewWorker.Processors;

/// <summary>
/// Rasterizes a PDF into inert page images with poppler (<c>pdfinfo</c>, <c>pdftoppm</c>). The browser never receives
/// the PDF itself as a preview: PDF can carry scripts, forms and external references, while a PNG of the page carries
/// none of them (DOCUMENT_MODEL.md §9.5 constraint 1). Shared by the PDF and Office processors, so a converted Word or
/// Excel file goes through exactly the same standard page pipeline.
/// </summary>
public static partial class PdfPageRenderer
{
    /// <summary>Probes poppler's version; null when a tool is missing.</summary>
    public static async Task<string?> PopplerVersionAsync(PreviewToolPaths tools, CancellationToken cancellationToken)
    {
        if (!Installed(tools.Pdftoppm) || !Installed(tools.Pdfinfo))
        {
            return null;
        }

        var result = await ToolRunner.RunAsync(tools.Pdftoppm!, ["-v"], tools, new PreviewLimits { ToolTimeoutSeconds = 20 }, Path.GetTempPath(), cancellationToken: cancellationToken);
        var match = VersionPattern().Match(result.StandardError + result.StandardOutput);
        return match.Success ? match.Groups[1].Value : "unknown";
    }

    public static async Task<ProcessorOutcome> RenderAsync(PreviewWorkContext context, string pdfPath, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var info = await ToolRunner.RunAsync(context.Tools.Pdfinfo!, [pdfPath], context, cancellationToken: cancellationToken);
        if (info.ExitCode != 0)
        {
            throw Classify(info, "The PDF could not be read.");
        }

        var pages = PagesPattern().Match(info.StandardOutput);
        if (!pages.Success || !int.TryParse(pages.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var pageCount) || pageCount < 1)
        {
            throw new PreviewFailureException(PreviewFailureCodes.Malformed, "The PDF has no readable pages.");
        }

        var rendered = Math.Min(pageCount, Math.Max(1, context.Limits.MaxPages));
        var pagesDirectory = Directory.CreateDirectory(Path.Combine(context.WorkDirectory, "pages")).FullName;
        var render = await ToolRunner.RunAsync(context.Tools.Pdftoppm!,
        [
            "-png",
            "-r", context.Limits.RenderDpi.ToString(CultureInfo.InvariantCulture),
            "-scale-to", context.Limits.MaxPreviewPixels.ToString(CultureInfo.InvariantCulture),
            "-f", "1",
            "-l", rendered.ToString(CultureInfo.InvariantCulture),
            pdfPath,
            Path.Combine(pagesDirectory, "p"),
        ], context, cancellationToken: cancellationToken);
        if (render.ExitCode != 0)
        {
            throw Classify(render, "The PDF pages could not be rendered.");
        }

        // pdftoppm pads page numbers to the width of the last page number (p-1.png, p-01.png, ...): read them back by
        // number and give each the fixed artifact name. Anything else in the directory is ignored.
        var produced = Directory.GetFiles(pagesDirectory, "p-*.png")
            .Select(path => (Path: path, Match: RenderedPagePattern().Match(Path.GetFileName(path))))
            .Where(item => item.Match.Success)
            .Select(item => (item.Path, Number: int.Parse(item.Match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture)))
            .Where(item => item.Number >= 1 && item.Number <= rendered)
            .OrderBy(item => item.Number)
            .ToArray();
        if (produced.Length != rendered)
        {
            throw new PreviewFailureException(PreviewFailureCodes.ConversionFailed, "Not every page could be rendered.");
        }

        foreach (var (path, number) in produced)
        {
            var name = PreviewArtifactFiles.Page(number);
            File.Move(path, context.OutputPath(name));
            var size = RasterHeader.Read(context.OutputPath(name))
                ?? throw new PreviewFailureException(PreviewFailureCodes.ConversionFailed, "A rendered page is not a valid image.");
            context.Record(name, PreviewArtifactKind.Page, "image/png", number, size.Width, size.Height);
        }

        var thumbnail = await ToolRunner.RunAsync(context.Tools.Pdftoppm!,
        [
            "-png", "-singlefile",
            "-scale-to", context.Limits.ThumbnailPixels.ToString(CultureInfo.InvariantCulture),
            "-f", "1", "-l", "1",
            pdfPath,
            Path.Combine(context.WorkDirectory, "thumbnail"),
        ], context, cancellationToken: cancellationToken);
        var thumbnailPath = Path.Combine(context.WorkDirectory, "thumbnail.png");
        if (thumbnail.ExitCode == 0 && File.Exists(thumbnailPath))
        {
            File.Move(thumbnailPath, context.OutputPath(PreviewArtifactFiles.Thumbnail));
            var size = RasterHeader.Read(context.OutputPath(PreviewArtifactFiles.Thumbnail));
            context.Record(PreviewArtifactFiles.Thumbnail, PreviewArtifactKind.Thumbnail, "image/png", width: size?.Width, height: size?.Height);
        }

        return new ProcessorOutcome(PreviewType.Pages, pageCount, rendered);
    }

    public static bool Installed(string? tool) => tool is { Length: > 0 } && Path.IsPathFullyQualified(tool) && File.Exists(tool);

    private static PreviewFailureException Classify(ToolResult result, string message)
    {
        var text = result.StandardError + result.StandardOutput;
        return text.Contains("password", StringComparison.OrdinalIgnoreCase) || text.Contains("encrypt", StringComparison.OrdinalIgnoreCase)
            ? new PreviewFailureException(PreviewFailureCodes.Encrypted, "The PDF is password-protected.")
            : new PreviewFailureException(PreviewFailureCodes.Malformed, message);
    }

    [GeneratedRegex(@"version ([0-9][0-9.]*)")]
    private static partial Regex VersionPattern();

    [GeneratedRegex(@"^Pages:\s+([0-9]{1,9})\s*$", RegexOptions.Multiline)]
    private static partial Regex PagesPattern();

    [GeneratedRegex(@"^p-([0-9]{1,9})\.png$")]
    private static partial Regex RenderedPagePattern();
}

/// <summary>PDF → page images and a first-page thumbnail.</summary>
public sealed class PdfPreviewProcessor : IDocumentPreviewProcessor
{
    public string ProcessorName => PreviewRouting.PdfProcessor;

    public string ImplementationVersion => "1";

    public bool CanProcess(string contentType) => PreviewRouting.Route(contentType).Processor == ProcessorName;

    public async Task<PreviewProcessorCapability> ProbeAsync(PreviewToolPaths tools, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tools);
        var poppler = await PdfPageRenderer.PopplerVersionAsync(tools, cancellationToken);
        return poppler is null
            ? new(ProcessorName, ImplementationVersion, false, "poppler-utils (pdfinfo, pdftoppm) is not installed")
            : new(ProcessorName, $"{ImplementationVersion};poppler={poppler}", true, null);
    }

    public async Task<ProcessorOutcome> GenerateAsync(PreviewWorkContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        context.RequireInputAtMost(context.Limits.MaxInputBytes);
        return await PdfPageRenderer.RenderAsync(context, context.InputPath, cancellationToken);
    }
}
