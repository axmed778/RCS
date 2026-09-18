using System.Globalization;
using System.Text;

namespace Rcs.Domain.Documents;

/// <summary>The business families the department must be able to retain (DECISIONS.md ADR-042).</summary>
public enum FileFamily
{
    Pdf = 1,
    Kmz,
    Word,
    Excel,
    AutoCad,

    /// <summary>
    /// ArchiCAD is a required family, but its exact extension and MIME mapping is NOT confirmed (ADR-042, OQ-D3). No
    /// extension is mapped to it; until the department confirms the list, an ArchiCAD file is stored as opaque bytes
    /// like any other unclassified file. The value exists so the family is named, not invented.
    /// </summary>
    ArchiCad,

    /// <summary>Accepted and stored as opaque bytes, download-only: class E (refused outright) stays empty.</summary>
    Unclassified,
}

/// <summary>What a bounded look at the leading bytes can prove about a file's container.</summary>
public enum ContentSignature
{
    Unknown = 0,
    Pdf,

    /// <summary>A ZIP container: OOXML documents and KMZ are ZIP files; the ZIP itself is never opened.</summary>
    Zip,

    /// <summary>An OLE compound file: legacy .doc / .xls.</summary>
    OleCompound,
    Dwg,
    Dxf,
}

/// <summary>One explicitly accepted extension, the family it belongs to and the container its content must show.</summary>
public sealed record AcceptedFormat(string Extension, FileFamily Family, string MimeType, ContentSignature Signature, bool IsMacroEnabled);

/// <summary>
/// The detected type of an upload. <see cref="MimeType"/> is what is stored in <c>document_version.mime_type</c>: it
/// comes from content, refined by the extension only when the extension agrees with the content (DOCUMENT_MODEL.md §9.2).
/// </summary>
/// <param name="ExtensionMatchesContent">
/// False when the original extension names a different container than the content shows — a mismatch that is recorded
/// in the upload audit event and surfaced, never a reason to reject (§9.2).
/// </param>
public sealed record DetectedFileType(
    string MimeType,
    FileFamily Family,
    ContentSignature Signature,
    string? OriginalExtension,
    bool ExtensionMatchesContent,
    bool IsMacroEnabled);

/// <summary>
/// The accepted-format framework of DOCUMENT_MODEL.md §9 and ADR-042. Everything here is <b>bounded signature
/// matching</b> over a fixed number of leading bytes: nothing is decompressed, parsed, rendered or executed, and the cost
/// does not depend on the file (SECURITY.md §10.1, ADR-035).
/// </summary>
/// <remarks>
/// The list of extensions is explicit, and it classifies rather than gatekeeps: ADR-042 keeps class E (forbidden)
/// empty, so an unrecognised file — including an ArchiCAD file while its mapping is unconfirmed — is accepted,
/// stored as <c>application/octet-stream</c>, and always delivered as a download.
/// </remarks>
public static class FileTypePolicy
{
    /// <summary>How many leading bytes detection may look at. Fixed, so detection cost never depends on the file.</summary>
    public const int SignatureLength = 512;

    /// <summary>500 MB per file (ADR-042), counted in binary megabytes: 500 × 1024 × 1024 bytes.</summary>
    public const long MaxUploadBytes = 500L * 1024 * 1024;

    public const string OpaqueMimeType = "application/octet-stream";

    private const string ZipMimeType = "application/zip";
    private const string OleMimeType = "application/x-ole-storage";

    /// <summary>
    /// The explicit accepted extensions. Word and Excel include the macro-enabled formats, which authorities send and
    /// which are therefore accepted, marked and download-only (OQ-D4 answered "accept"). AutoCAD is .dwg / .dxf
    /// (DOCUMENT_MODEL.md §9.3). ArchiCAD has no row until its mapping is confirmed.
    /// </summary>
    public static readonly IReadOnlyList<AcceptedFormat> AcceptedFormats =
    [
        new(".pdf", FileFamily.Pdf, "application/pdf", ContentSignature.Pdf, false),
        new(".kmz", FileFamily.Kmz, "application/vnd.google-earth.kmz", ContentSignature.Zip, false),
        new(".doc", FileFamily.Word, "application/msword", ContentSignature.OleCompound, false),
        new(".docx", FileFamily.Word, "application/vnd.openxmlformats-officedocument.wordprocessingml.document", ContentSignature.Zip, false),
        new(".docm", FileFamily.Word, "application/vnd.ms-word.document.macroEnabled.12", ContentSignature.Zip, true),
        new(".xls", FileFamily.Excel, "application/vnd.ms-excel", ContentSignature.OleCompound, false),
        new(".xlsx", FileFamily.Excel, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", ContentSignature.Zip, false),
        new(".xlsm", FileFamily.Excel, "application/vnd.ms-excel.sheet.macroEnabled.12", ContentSignature.Zip, true),
        new(".dwg", FileFamily.AutoCad, "image/vnd.dwg", ContentSignature.Dwg, false),
        new(".dxf", FileFamily.AutoCad, "image/vnd.dxf", ContentSignature.Dxf, false),
    ];

    private static readonly byte[] PdfMagic = "%PDF-"u8.ToArray();
    private static readonly byte[] ZipMagic = [0x50, 0x4B, 0x03, 0x04];
    private static readonly byte[] ZipEmptyMagic = [0x50, 0x4B, 0x05, 0x06];
    private static readonly byte[] OleMagic = [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1];
    private static readonly byte[] BinaryDxfMagic = "AutoCAD Binary DXF\r\n\0"u8.ToArray();

    /// <summary>The lower-case extension of a file name (".pdf"), or null. The extension never decides handling.</summary>
    public static string? ExtensionOf(string? fileName)
    {
        if (string.IsNullOrEmpty(fileName))
        {
            return null;
        }

        var name = fileName.Replace('\\', '/');
        name = name[(name.LastIndexOf('/') + 1)..];
        var dot = name.LastIndexOf('.');
        if (dot <= 0 || dot == name.Length - 1)
        {
            return null;
        }

        var extension = name[dot..].ToLowerInvariant();
        return extension.Length <= 12 && extension[1..].All(char.IsAsciiLetterOrDigit) ? extension : null;
    }

    /// <summary>Compares the leading bytes with the fixed signature table. Never reads beyond <see cref="SignatureLength"/>.</summary>
    public static ContentSignature Sniff(ReadOnlySpan<byte> head)
    {
        if (head.Length > SignatureLength)
        {
            head = head[..SignatureLength];
        }

        if (head.StartsWith(PdfMagic))
        {
            return ContentSignature.Pdf;
        }

        if (head.StartsWith(ZipMagic) || head.StartsWith(ZipEmptyMagic))
        {
            return ContentSignature.Zip;
        }

        if (head.StartsWith(OleMagic))
        {
            return ContentSignature.OleCompound;
        }

        // DWG: "AC" followed by the release code, e.g. AC1015, AC1032 (and the early AC1.40 / AC2.10 forms).
        if (head.Length >= 6 && head[0] == (byte)'A' && head[1] == (byte)'C'
            && (head[2] is (byte)'1' or (byte)'2') && IsDigitOrDot(head[3]) && IsDigitOrDot(head[4]) && IsDigitOrDot(head[5]))
        {
            return ContentSignature.Dwg;
        }

        if (head.StartsWith(BinaryDxfMagic) || LooksLikeAsciiDxf(head))
        {
            return ContentSignature.Dxf;
        }

        return ContentSignature.Unknown;
    }

    /// <summary>Content decides; the extension may only refine a container family it agrees with (DOCUMENT_MODEL.md §9.2).</summary>
    public static DetectedFileType Detect(ReadOnlySpan<byte> head, string? originalFileName)
    {
        var signature = Sniff(head);
        var extension = ExtensionOf(originalFileName);
        var claimed = extension is null ? null : AcceptedFormats.FirstOrDefault(format => format.Extension == extension);

        if (claimed is not null && claimed.Signature == signature)
        {
            return new DetectedFileType(claimed.MimeType, claimed.Family, signature, extension, true, claimed.IsMacroEnabled);
        }

        // The extension names a known format whose container the content does not show, or names nothing we know.
        var extensionMatches = claimed is null && signature == ContentSignature.Unknown;
        return signature switch
        {
            ContentSignature.Pdf => new DetectedFileType("application/pdf", FileFamily.Pdf, signature, extension, extension == ".pdf", false),
            ContentSignature.Dwg => new DetectedFileType("image/vnd.dwg", FileFamily.AutoCad, signature, extension, extension == ".dwg", false),
            ContentSignature.Dxf => new DetectedFileType("image/vnd.dxf", FileFamily.AutoCad, signature, extension, extension == ".dxf", false),

            // A ZIP or OLE container whose extension does not say which member format it is stays the container.
            ContentSignature.Zip => new DetectedFileType(ZipMimeType, FileFamily.Unclassified, signature, extension, extension == ".zip", false),
            ContentSignature.OleCompound => new DetectedFileType(OleMimeType, FileFamily.Unclassified, signature, extension, claimed is null, false),
            _ => new DetectedFileType(OpaqueMimeType, FileFamily.Unclassified, signature, extension, extensionMatches, false),
        };
    }

    /// <summary>Whether a stored type is a macro-enabled Office format, to be marked visibly (ADR-042).</summary>
    public static bool IsMacroEnabled(string mimeType) =>
        AcceptedFormats.Any(format => format.IsMacroEnabled && string.Equals(format.MimeType, mimeType, StringComparison.OrdinalIgnoreCase));

    /// <summary>The family a stored MIME type belongs to.</summary>
    public static FileFamily FamilyOf(string mimeType) =>
        AcceptedFormats.FirstOrDefault(format => string.Equals(format.MimeType, mimeType, StringComparison.OrdinalIgnoreCase))?.Family
        ?? FileFamily.Unclassified;

    /// <summary>
    /// The download name, built at download time and never stored (DOCUMENT_MODEL.md §5.6): the sanitised title (or the
    /// original name when the title is empty), stripped of control, bidi and path characters, with the canonical
    /// extension of the <b>detected</b> type. A file whose content is unclassified keeps its original extension when
    /// that extension is a plain token — there is no detected type for it to disagree with.
    /// </summary>
    public static string SafeDownloadFileName(string? title, string originalFileName, string mimeType)
    {
        var canonical = CanonicalExtension(mimeType) ?? ExtensionOf(originalFileName);
        var baseName = Sanitize(string.IsNullOrWhiteSpace(title) ? StripExtension(originalFileName) : title);
        if (baseName.Length == 0)
        {
            baseName = "document";
        }

        if (canonical is not null && baseName.EndsWith(canonical, StringComparison.OrdinalIgnoreCase))
        {
            baseName = baseName[..^canonical.Length].TrimEnd('.', ' ');
            if (baseName.Length == 0)
            {
                baseName = "document";
            }
        }

        return baseName + (canonical ?? string.Empty);
    }

    /// <summary>Removes control, format (bidi) and path characters for display. The stored original stays byte-exact.</summary>
    public static string Sanitize(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(value.Length);
        foreach (var rune in value.EnumerateRunes())
        {
            var category = Rune.GetUnicodeCategory(rune);
            if (category is UnicodeCategory.Control or UnicodeCategory.Format or UnicodeCategory.Surrogate
                or UnicodeCategory.PrivateUse or UnicodeCategory.OtherNotAssigned or UnicodeCategory.LineSeparator
                or UnicodeCategory.ParagraphSeparator)
            {
                continue;
            }

            builder.Append(rune.Value switch
            {
                '/' or '\\' or ':' or '*' or '?' or '"' or '<' or '>' or '|' => "_",
                _ => rune.ToString(),
            });
        }

        var collapsed = string.Join(' ', builder.ToString().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).Trim(' ', '.');
        return collapsed.Length <= 150 ? collapsed : collapsed[..150].TrimEnd(' ', '.');
    }

    private static string? CanonicalExtension(string mimeType)
    {
        if (string.Equals(mimeType, ZipMimeType, StringComparison.OrdinalIgnoreCase))
        {
            return ".zip";
        }

        var matches = AcceptedFormats.Where(format => string.Equals(format.MimeType, mimeType, StringComparison.OrdinalIgnoreCase)).ToArray();
        return matches.Length == 1 ? matches[0].Extension : null;
    }

    private static string StripExtension(string fileName)
    {
        var extension = ExtensionOf(fileName);
        var name = fileName.Replace('\\', '/');
        name = name[(name.LastIndexOf('/') + 1)..];
        return extension is null ? name : name[..^extension.Length];
    }

    private static bool IsDigitOrDot(byte value) => value is (>= (byte)'0' and <= (byte)'9') or (byte)'.';

    /// <summary>ASCII DXF opens with group code 0 and the SECTION keyword, possibly after whitespace.</summary>
    private static bool LooksLikeAsciiDxf(ReadOnlySpan<byte> head)
    {
        var index = 0;
        while (index < head.Length && head[index] is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n')
        {
            index++;
        }

        if (index >= head.Length || head[index] != (byte)'0')
        {
            return false;
        }

        index++;
        while (index < head.Length && head[index] is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n')
        {
            index++;
        }

        return head[index..].StartsWith("SECTION"u8);
    }
}
