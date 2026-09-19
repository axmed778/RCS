using Rcs.Application.Previews;
using Rcs.Domain.Documents;
using Rcs.PreviewWorker.Processing;

namespace Rcs.PreviewWorker.Processors;

/// <summary>
/// DWG. RCS does not parse DWG itself (ADR-044), and no approved local converter is installed: LibreDWG is not
/// packaged for the supported server OS and is itself an experimental parser; the ODA File Converter is proprietary
/// freeware whose licence and offline redistribution have not been approved. The adapter exists so that a converter
/// can be added later behind this name (DWG → DXF, then the DXF reader) without redesign. Until then every DWG version
/// is UNSUPPORTED — stored, downloadable, honestly not previewed.
/// </summary>
public sealed class DwgPreviewProcessor : IDocumentPreviewProcessor
{
    public const string Blocker = "no approved local DWG converter (LibreDWG is not packaged for this OS; ODA File Converter licence not approved)";

    public string ProcessorName => PreviewRouting.DwgProcessor;

    public string ImplementationVersion => "0";

    public bool CanProcess(string contentType) => PreviewRouting.Route(contentType).Processor == ProcessorName;

    public Task<PreviewProcessorCapability> ProbeAsync(PreviewToolPaths tools, CancellationToken cancellationToken) =>
        Task.FromResult(new PreviewProcessorCapability(ProcessorName, ImplementationVersion, false, Blocker));

    public Task<ProcessorOutcome> GenerateAsync(PreviewWorkContext context, CancellationToken cancellationToken) =>
        throw new PreviewFailureException(PreviewFailureCodes.ProcessorUnavailable, "No DWG converter is available.", unsupported: true);
}

/// <summary>
/// ArchiCAD (PLN/PLA/…). Its extension and MIME mapping is still unconfirmed (ADR-042) and there is no local,
/// Graphisoft-compatible renderer. Nothing is invented: this placeholder names the capability so support can be added
/// behind it later; today every ArchiCAD file is stored as opaque bytes and its preview is UNSUPPORTED.
/// </summary>
public sealed class ArchicadPreviewProcessor : IDocumentPreviewProcessor
{
    public const string Blocker = "ArchiCAD file mapping unconfirmed (ADR-042) and no local Graphisoft-compatible renderer exists";

    public string ProcessorName => PreviewRouting.ArchicadProcessor;

    public string ImplementationVersion => "0";

    public bool CanProcess(string contentType) => PreviewRouting.Route(contentType).Processor == ProcessorName;

    public Task<PreviewProcessorCapability> ProbeAsync(PreviewToolPaths tools, CancellationToken cancellationToken) =>
        Task.FromResult(new PreviewProcessorCapability(ProcessorName, ImplementationVersion, false, Blocker));

    public Task<ProcessorOutcome> GenerateAsync(PreviewWorkContext context, CancellationToken cancellationToken) =>
        throw new PreviewFailureException(PreviewFailureCodes.ProcessorUnavailable, "No ArchiCAD renderer is available.", unsupported: true);
}

/// <summary>Every processor the worker knows, in routing order.</summary>
public static class PreviewProcessors
{
    public static IReadOnlyList<IDocumentPreviewProcessor> All { get; } =
    [
        new PdfPreviewProcessor(),
        new RasterImagePreviewProcessor(),
        new OfficePreviewProcessor(),
        new KmzPreviewProcessor(),
        new DxfPreviewProcessor(),
        new DwgPreviewProcessor(),
        new ArchicadPreviewProcessor(),
    ];

    public static IDocumentPreviewProcessor? Find(string name) =>
        All.FirstOrDefault(processor => string.Equals(processor.ProcessorName, name, StringComparison.Ordinal));
}
