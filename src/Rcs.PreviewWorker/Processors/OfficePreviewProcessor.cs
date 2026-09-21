using System.Text.RegularExpressions;
using Rcs.Application.Previews;
using Rcs.Domain.Documents;
using Rcs.PreviewWorker.Processing;

namespace Rcs.PreviewWorker.Processors;

/// <summary>
/// Word and Excel (DOC/DOCX/XLS/XLSX) → PDF with a headless LibreOffice, then the standard PDF page pipeline. LibreOffice
/// runs with a fresh, throw-away profile in the job's work directory that disables macro execution; the sandbox takes
/// away the network, so external links and remote images cannot be fetched either. The document is never rebuilt as
/// HTML: the page images are what LibreOffice would print.
/// </summary>
public sealed partial class OfficePreviewProcessor : IDocumentPreviewProcessor
{
    /// <summary>A throw-away user profile: macros never run, no first-start wizard, no update checks, no recovery.</summary>
    private const string ProfileSettings = """
        <?xml version="1.0" encoding="UTF-8"?>
        <oor:items xmlns:oor="http://openoffice.org/2001/registry" xmlns:xs="http://www.w3.org/2001/XMLSchema" xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance">
        <item oor:path="/org.openoffice.Office.Common/Security/Scripting"><prop oor:name="MacroSecurityLevel" oor:op="fuse"><value>3</value></prop></item>
        <item oor:path="/org.openoffice.Office.Common/Security/Scripting"><prop oor:name="DisableMacrosExecution" oor:op="fuse"><value>true</value></prop></item>
        <item oor:path="/org.openoffice.Office.Common/Misc"><prop oor:name="FirstRun" oor:op="fuse"><value>false</value></prop></item>
        <item oor:path="/org.openoffice.Office.Common/Misc"><prop oor:name="UseLocking" oor:op="fuse"><value>false</value></prop></item>
        <item oor:path="/org.openoffice.Office.Recovery/RecoveryInfo"><prop oor:name="Enabled" oor:op="fuse"><value>false</value></prop></item>
        <item oor:path="/org.openoffice.Office.Recovery/AutoSave"><prop oor:name="Enabled" oor:op="fuse"><value>false</value></prop></item>
        <item oor:path="/org.openoffice.Office.Update/Update"><prop oor:name="AutoCheckEnabled" oor:op="fuse"><value>false</value></prop></item>
        </oor:items>
        """;

    public string ProcessorName => PreviewRouting.OfficeProcessor;

    public string ImplementationVersion => "1";

    public bool CanProcess(string contentType) => PreviewRouting.Route(contentType, allowMacroEnabledOffice: true).Processor == ProcessorName;

    public async Task<PreviewProcessorCapability> ProbeAsync(PreviewToolPaths tools, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tools);
        var poppler = await PdfPageRenderer.PopplerVersionAsync(tools, cancellationToken);
        if (!PdfPageRenderer.Installed(tools.Soffice))
        {
            return new(ProcessorName, ImplementationVersion, false, "LibreOffice (soffice) is not installed");
        }

        if (poppler is null)
        {
            return new(ProcessorName, ImplementationVersion, false, "poppler-utils (pdfinfo, pdftoppm) is not installed");
        }

        var home = Directory.CreateTempSubdirectory("rcs-probe-").FullName;
        var result = await ToolRunner.RunAsync(tools.Soffice!, ["--headless", "--version"], tools, new PreviewLimits { ToolTimeoutSeconds = 60 }, home, cancellationToken: cancellationToken);
        var match = VersionPattern().Match(result.StandardOutput);
        return match.Success
            ? new(ProcessorName, $"{ImplementationVersion};libreoffice={match.Groups[1].Value};poppler={poppler}", true, null)
            : new(ProcessorName, ImplementationVersion, false, "LibreOffice did not report a version");
    }

    public async Task<ProcessorOutcome> GenerateAsync(PreviewWorkContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        context.RequireInputAtMost(context.Limits.MaxInputBytes);
        if (!PdfPageRenderer.Installed(context.Tools.Soffice))
        {
            throw new PreviewFailureException(PreviewFailureCodes.ProcessorUnavailable, "LibreOffice is not installed.", unsupported: true);
        }

        var profile = Directory.CreateDirectory(Path.Combine(context.WorkDirectory, "lo-profile", "user")).FullName;
        await File.WriteAllTextAsync(Path.Combine(profile, "registrymodifications.xcu"), ProfileSettings, cancellationToken);
        var converted = Directory.CreateDirectory(Path.Combine(context.WorkDirectory, "converted")).FullName;

        var result = await ToolRunner.RunAsync(context.Tools.Soffice!,
        [
            "--headless", "--invisible", "--norestore", "--nologo", "--nodefault", "--nolockcheck", "--nofirststartwizard",
            "-env:UserInstallation=" + new Uri(Path.Combine(context.WorkDirectory, "lo-profile")).AbsoluteUri,
            "--convert-to", "pdf",
            "--outdir", converted,
            context.InputPath,
        ], context, cancellationToken: cancellationToken);

        // soffice reports success on stdout and may still exit 0 without output (an unreadable file): the PDF decides.
        var pdf = Path.Combine(converted, Path.GetFileNameWithoutExtension(context.InputPath) + ".pdf");
        if (!File.Exists(pdf) || new FileInfo(pdf).Length == 0)
        {
            var text = result.StandardError + result.StandardOutput;
            throw text.Contains("password", StringComparison.OrdinalIgnoreCase)
                ? new PreviewFailureException(PreviewFailureCodes.Encrypted, "The document is password-protected.")
                : new PreviewFailureException(PreviewFailureCodes.ConversionFailed, "The document could not be converted to PDF.");
        }

        return await PdfPageRenderer.RenderAsync(context, pdf, cancellationToken);
    }

    [GeneratedRegex(@"LibreOffice ([0-9][0-9.]*)")]
    private static partial Regex VersionPattern();
}
