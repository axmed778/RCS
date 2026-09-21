using System.Globalization;
using System.IO.Compression;
using System.Text.RegularExpressions;
using System.Xml;
using Rcs.Application.Previews;
using Rcs.Domain.Documents;
using Rcs.PreviewWorker.Processing;

namespace Rcs.PreviewWorker.Processors;

/// <summary>
/// KMZ → a geometry document of its placemarks (points, lines, polygons), drawn by the RCS viewer without any basemap,
/// tile server or external API. The archive is never extracted to disk: its directory is validated (entry count, names,
/// declared sizes, compression ratio), then exactly one KML entry is streamed through a byte counter into an XML reader
/// that refuses DTDs and external entities.
/// </summary>
public sealed partial class KmzPreviewProcessor : IDocumentPreviewProcessor
{
    public string ProcessorName => PreviewRouting.KmzProcessor;

    public string ImplementationVersion => "1";

    public bool CanProcess(string contentType) => PreviewRouting.Route(contentType).Processor == ProcessorName;

    public Task<PreviewProcessorCapability> ProbeAsync(PreviewToolPaths tools, CancellationToken cancellationToken) =>
        Task.FromResult(new PreviewProcessorCapability(ProcessorName, $"{ImplementationVersion};kml-reader=1", true, null));

    public async Task<ProcessorOutcome> GenerateAsync(PreviewWorkContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        context.RequireInputAtMost(context.Limits.MaxInputBytes);
        var geometry = new GeometryBuilder(context.Limits, geographic: true);

        ZipArchive archive;
        try
        {
            archive = new ZipArchive(new FileStream(context.InputPath, FileMode.Open, FileAccess.Read, FileShare.Read), ZipArchiveMode.Read);
        }
        catch (InvalidDataException)
        {
            throw new PreviewFailureException(PreviewFailureCodes.Malformed, "The KMZ is not a readable ZIP archive.");
        }

        using (archive)
        {
            var kml = SelectKml(archive, context.Limits);
            await using var stream = new LimitedReadStream(kml.Open(), context.Limits.KmlMaxBytes);
            try
            {
                Read(stream, geometry, context.Limits);
            }
            catch (XmlException)
            {
                throw new PreviewFailureException(PreviewFailureCodes.Malformed, "The KML inside the KMZ is not well-formed XML.");
            }
            catch (InvalidDataException)
            {
                throw new PreviewFailureException(PreviewFailureCodes.Malformed, "The KMZ entry could not be decompressed.");
            }
        }

        await geometry.WriteAsync(context, "kmz", null, cancellationToken);
        return new ProcessorOutcome(PreviewType.Geometry);
    }

    /// <summary>
    /// Validates the whole central directory before anything is decompressed, then picks the KML: <c>doc.kml</c> at the
    /// root (the KMZ convention), else the first root-level <c>.kml</c>, else the first <c>.kml</c> anywhere.
    /// </summary>
    public static ZipArchiveEntry SelectKml(ZipArchive archive, PreviewLimits limits)
    {
        ArgumentNullException.ThrowIfNull(archive);
        ArgumentNullException.ThrowIfNull(limits);
        if (archive.Entries.Count > limits.KmzMaxEntries)
        {
            throw new PreviewFailureException(PreviewFailureCodes.ArchiveTooLarge, "The KMZ contains too many entries.");
        }

        long declared = 0;
        foreach (var entry in archive.Entries)
        {
            if (!IsSafeEntryName(entry.FullName))
            {
                throw new PreviewFailureException(PreviewFailureCodes.ArchiveUnsafe, "The KMZ contains an unsafe entry path.");
            }

            declared += entry.Length;
            if (entry.Length < 0 || declared > limits.KmzMaxUncompressedBytes)
            {
                throw new PreviewFailureException(PreviewFailureCodes.ArchiveTooLarge, "The KMZ expands beyond the decompression limit.");
            }

            if (entry.Length > 1024 * 1024 && entry.Length / Math.Max(1, entry.CompressedLength) > limits.KmzMaxCompressionRatio)
            {
                throw new PreviewFailureException(PreviewFailureCodes.ArchiveUnsafe, "The KMZ has a suspicious compression ratio.");
            }
        }

        var kmls = archive.Entries.Where(entry => entry.FullName.EndsWith(".kml", StringComparison.OrdinalIgnoreCase)).ToArray();
        return kmls.FirstOrDefault(entry => string.Equals(entry.FullName, "doc.kml", StringComparison.OrdinalIgnoreCase))
            ?? kmls.FirstOrDefault(entry => !entry.FullName.Contains('/', StringComparison.Ordinal))
            ?? kmls.FirstOrDefault()
            ?? throw new PreviewFailureException(PreviewFailureCodes.NoGeometry, "The KMZ contains no KML document.");
    }

    /// <summary>A relative, forward-slash path with no traversal, no root, no drive and no control characters.</summary>
    public static bool IsSafeEntryName(string name)
    {
        if (string.IsNullOrEmpty(name) || name.Length > 512 || name[0] is '/' or '\\' || name.Contains('\\', StringComparison.Ordinal)
            || name.Contains(':', StringComparison.Ordinal) || name.Any(char.IsControl))
        {
            return false;
        }

        return name.Split('/').All(segment => segment is not (".." or "."));
    }

    /// <summary>Reads Placemarks with a forward-only reader. Namespaces are ignored so KML 2.1, 2.2 and gx: all work.</summary>
    public static void Read(Stream kml, GeometryBuilder geometry, PreviewLimits limits)
    {
        ArgumentNullException.ThrowIfNull(geometry);
        ArgumentNullException.ThrowIfNull(limits);
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersFromEntities = 0,
            MaxCharactersInDocument = limits.KmlMaxBytes,
            IgnoreComments = true,
            IgnoreProcessingInstructions = true,
            IgnoreWhitespace = true,
            CloseInput = false,
        };

        using var reader = XmlReader.Create(kml, settings);
        var containers = new Stack<string?>();

        // ReadElementContentAsString and Skip already move to the next node; that node must be examined, not read past.
        var positioned = false;
        while (!geometry.IsFull && (positioned || reader.Read()))
        {
            positioned = false;
            if (reader.NodeType == XmlNodeType.EndElement && reader.LocalName is "Folder" or "Document")
            {
                if (containers.Count > 0)
                {
                    containers.Pop();
                }

                continue;
            }

            if (reader.NodeType != XmlNodeType.Element)
            {
                continue;
            }

            switch (reader.LocalName)
            {
                case "Folder" or "Document" when !reader.IsEmptyElement:
                    containers.Push(null);
                    break;
                case "name" when containers.Count > 0 && containers.Peek() is null:
                    containers.Pop();
                    containers.Push(reader.ReadElementContentAsString());
                    positioned = true;
                    break;
                case "Placemark" when !reader.IsEmptyElement:
                    ReadPlacemark(reader.ReadSubtree(), geometry, containers.FirstOrDefault(name => name is not null));
                    break;
            }
        }
    }

    private static void ReadPlacemark(XmlReader placemark, GeometryBuilder geometry, string? folder)
    {
        using var reader = placemark;
        string? name = null;
        string? description = null;
        var shapes = new List<(string Kind, List<List<GeoPoint>> Rings)>();
        List<List<GeoPoint>>? polygon = null;
        List<GeoPoint>? track = null;
        var positioned = false;

        reader.Read(); // <Placemark>
        while (positioned || reader.Read())
        {
            positioned = false;
            if (reader.EOF)
            {
                break;
            }

            if (reader.NodeType == XmlNodeType.EndElement)
            {
                if (reader.LocalName == "Polygon" && polygon is not null)
                {
                    shapes.Add(("polygon", polygon));
                    polygon = null;
                }
                else if (reader.LocalName == "Track" && track is not null)
                {
                    shapes.Add(("line", [track]));
                    track = null;
                }

                continue;
            }

            if (reader.NodeType != XmlNodeType.Element)
            {
                continue;
            }

            switch (reader.LocalName)
            {
                case "name" when name is null:
                    name = reader.ReadElementContentAsString();
                    positioned = true;
                    break;
                case "description" when description is null:
                    description = StripMarkup(reader.ReadElementContentAsString());
                    positioned = true;
                    break;
                case "Polygon":
                    polygon = [];
                    break;
                case "Track":
                    track = [];
                    break;
                case "coord" when track is not null:
                    var parts = reader.ReadElementContentAsString().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                    positioned = true;
                    if (parts.Length >= 2 && TryCoordinate(parts[0], parts[1], out var point))
                    {
                        track.Add(point);
                    }

                    break;
                case "coordinates":
                    var points = ParseCoordinates(reader.ReadElementContentAsString(), geometry);
                    positioned = true;
                    if (polygon is not null)
                    {
                        polygon.Add(points);
                    }
                    else
                    {
                        shapes.Add((points.Count == 1 ? "point" : "line", [points]));
                    }

                    break;
                case "ExtendedData" or "Style" or "StyleMap" or "Region" or "LookAt" or "Camera":
                    reader.Skip();
                    positioned = true;
                    break;
            }
        }

        foreach (var (kind, rings) in shapes)
        {
            switch (kind)
            {
                case "point" when rings[0].Count == 1:
                    geometry.AddPoint(rings[0][0], name, description, folder);
                    break;
                case "line":
                    geometry.AddLine(rings[0], name, description, folder);
                    break;
                case "polygon":
                    geometry.AddPolygon(rings.Cast<IReadOnlyList<GeoPoint>>().ToArray(), name, description, folder);
                    break;
            }
        }
    }

    /// <summary>KML tuples are <c>lon,lat[,alt]</c> separated by whitespace. Out-of-range tuples are skipped and counted.</summary>
    public static List<GeoPoint> ParseCoordinates(string text, GeometryBuilder geometry)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(geometry);
        var points = new List<GeoPoint>();
        foreach (var tuple in text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = tuple.Split(',');
            if (parts.Length >= 2 && TryCoordinate(parts[0], parts[1], out var point))
            {
                points.Add(point);
            }
            else
            {
                geometry.Skipped++;
            }
        }

        return points;
    }

    private static bool TryCoordinate(string longitude, string latitude, out GeoPoint point)
    {
        point = default;
        if (double.TryParse(longitude, NumberStyles.Float, CultureInfo.InvariantCulture, out var x)
            && double.TryParse(latitude, NumberStyles.Float, CultureInfo.InvariantCulture, out var y)
            && x is >= -180 and <= 180 && y is >= -90 and <= 90)
        {
            point = new GeoPoint(x, y);
            return true;
        }

        return false;
    }

    /// <summary>Descriptions are often HTML. Only their text is kept; the viewer shows it as text, never as markup.</summary>
    private static string StripMarkup(string html) => System.Net.WebUtility.HtmlDecode(TagPattern().Replace(html, " "));

    [GeneratedRegex("<[^>]{0,2000}>")]
    private static partial Regex TagPattern();
}

/// <summary>
/// Counts the bytes actually decompressed and stops at a hard limit — the declared sizes in a ZIP directory are
/// attacker-controlled, so they are checked but never trusted.
/// </summary>
public sealed class LimitedReadStream(Stream inner, long limit) : Stream
{
    private long total;

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => total;
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count) => Count(inner.Read(buffer, offset, count));

    public override int Read(Span<byte> buffer) => Count(inner.Read(buffer));

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
        Count(await inner.ReadAsync(buffer, cancellationToken));

    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        Count(await inner.ReadAsync(buffer.AsMemory(offset, count), cancellationToken));

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            inner.Dispose();
        }

        base.Dispose(disposing);
    }

    private int Count(int read)
    {
        total += read;
        return total > limit
            ? throw new PreviewFailureException(PreviewFailureCodes.ArchiveTooLarge, "The KMZ expands beyond the decompression limit.")
            : read;
    }
}
