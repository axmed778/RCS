using System.Globalization;
using System.Text;
using System.Text.Json;
using Rcs.Application.Previews;
using Rcs.Domain.Documents;
using Rcs.PreviewWorker.Processing;

namespace Rcs.PreviewWorker.Processors;

/// <summary>A 2-D point in the geometry document's coordinate system (longitude/latitude, or drawing units).</summary>
public readonly record struct GeoPoint(double X, double Y);

/// <summary>
/// Collects extracted geometry into the <c>rcs-geometry/1</c> document the RCS viewer draws itself (a KMZ map without a
/// basemap, a DXF drawing). The document is DATA — numbers and plain strings — never markup: the viewer creates every
/// element with DOM APIs and sets names with <c>textContent</c>, so nothing in a KML or DXF can execute in the browser.
/// Limits on features and coordinates are enforced as geometry is added; beyond them the document is marked truncated.
/// </summary>
public sealed class GeometryBuilder
{
    private const int MaxTextLength = 500;
    private readonly List<Feature> features = [];
    private readonly List<string> groups = [];
    private readonly Dictionary<string, int> groupIndex = new(StringComparer.Ordinal);
    private readonly PreviewLimits limits;
    private readonly int decimals;
    private double minX = double.PositiveInfinity;
    private double minY = double.PositiveInfinity;
    private double maxX = double.NegativeInfinity;
    private double maxY = double.NegativeInfinity;
    private int coordinates;

    public GeometryBuilder(PreviewLimits limits, bool geographic)
    {
        ArgumentNullException.ThrowIfNull(limits);
        this.limits = limits;
        Geographic = geographic;
        decimals = geographic ? 7 : 4;
    }

    public bool Geographic { get; }

    public bool Truncated { get; private set; }

    public int Skipped { get; set; }

    public int FeatureCount => features.Count;

    public int CoordinateCount => coordinates;

    public bool IsFull => Truncated;

    public void AddPoint(GeoPoint point, string? name, string? description, string? group) =>
        Add(new Feature("point", [[point]], name, description, group, null, 0, 0));

    public void AddLine(IReadOnlyList<GeoPoint> points, string? name, string? description, string? group)
    {
        if (points.Count >= 2)
        {
            Add(new Feature("line", [points], name, description, group, null, 0, 0));
        }
        else
        {
            Skipped++;
        }
    }

    public void AddPolygon(IReadOnlyList<IReadOnlyList<GeoPoint>> rings, string? name, string? description, string? group)
    {
        var valid = rings.Where(ring => ring.Count >= 3).ToArray();
        if (valid.Length > 0)
        {
            Add(new Feature("polygon", valid, name, description, group, null, 0, 0));
        }
        else
        {
            Skipped++;
        }
    }

    public void AddText(GeoPoint at, string text, double height, double rotation, string? group)
    {
        var clean = Clean(text);
        if (clean is { Length: > 0 })
        {
            Add(new Feature("text", [[at]], null, null, group, clean, height, rotation));
        }
    }

    private void Add(Feature feature)
    {
        if (Truncated)
        {
            return;
        }

        var count = feature.Parts.Sum(part => part.Count);
        if (features.Count >= limits.MaxFeatures || coordinates + count > limits.MaxCoordinates)
        {
            Truncated = true;
            return;
        }

        foreach (var point in feature.Parts.SelectMany(part => part))
        {
            if (!double.IsFinite(point.X) || !double.IsFinite(point.Y))
            {
                Skipped++;
                return;
            }
        }

        foreach (var point in feature.Parts.SelectMany(part => part))
        {
            minX = Math.Min(minX, point.X);
            minY = Math.Min(minY, point.Y);
            maxX = Math.Max(maxX, point.X);
            maxY = Math.Max(maxY, point.Y);
        }

        coordinates += count;
        var group = Clean(feature.Group);
        if (group is not null && !groupIndex.ContainsKey(group))
        {
            groupIndex[group] = groups.Count;
            groups.Add(group);
        }

        features.Add(feature with { Name = Clean(feature.Name), Description = Clean(feature.Description), Group = group });
    }

    /// <summary>Writes the geometry document and, when libvips can render SVG, a thumbnail drawn from the same data.</summary>
    public async Task WriteAsync(PreviewWorkContext context, string source, string? units, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (features.Count == 0)
        {
            throw new PreviewFailureException(PreviewFailureCodes.NoGeometry, "No displayable geometry was found.");
        }

        var path = context.OutputPath(PreviewArtifactFiles.Geometry);
        await using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write))
        await using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("format", "rcs-geometry/1");
            writer.WriteString("source", source);
            writer.WriteString("crs", Geographic ? "WGS84" : "DRAWING");
            if (units is not null)
            {
                writer.WriteString("units", units);
            }

            writer.WriteStartArray("bounds");
            writer.WriteNumberValue(Round(minX));
            writer.WriteNumberValue(Round(minY));
            writer.WriteNumberValue(Round(maxX));
            writer.WriteNumberValue(Round(maxY));
            writer.WriteEndArray();
            writer.WriteBoolean("truncated", Truncated);
            writer.WriteStartObject("stats");
            foreach (var kind in new[] { "point", "line", "polygon", "text" })
            {
                writer.WriteNumber(kind, features.Count(feature => feature.Kind == kind));
            }

            writer.WriteNumber("coordinates", coordinates);
            writer.WriteNumber("skipped", Skipped);
            writer.WriteEndObject();

            writer.WriteStartArray("groups");
            foreach (var group in groups)
            {
                writer.WriteStringValue(group);
            }

            writer.WriteEndArray();
            writer.WriteStartArray("features");
            foreach (var feature in features)
            {
                WriteFeature(writer, feature);
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        context.Record(PreviewArtifactFiles.Geometry, PreviewArtifactKind.Geometry, "application/json");
        await WriteThumbnailAsync(context, cancellationToken);
    }

    private void WriteFeature(Utf8JsonWriter writer, Feature feature)
    {
        writer.WriteStartObject();
        writer.WriteString("t", feature.Kind);
        if (feature.Name is { Length: > 0 })
        {
            writer.WriteString("n", feature.Name);
        }

        if (feature.Description is { Length: > 0 })
        {
            writer.WriteString("d", feature.Description);
        }

        if (feature.Group is { Length: > 0 } && groupIndex.TryGetValue(feature.Group, out var index))
        {
            writer.WriteNumber("g", index);
        }

        if (feature.Text is { } text)
        {
            writer.WriteString("s", text);
            writer.WriteNumber("h", Round(feature.TextHeight));
            writer.WriteNumber("r", Math.Round(feature.Rotation, 2));
        }

        writer.WritePropertyName("c");
        switch (feature.Kind)
        {
            case "point" or "text":
                WritePoint(writer, feature.Parts[0][0]);
                break;
            case "line":
                WriteRing(writer, feature.Parts[0]);
                break;
            default:
                writer.WriteStartArray();
                foreach (var ring in feature.Parts)
                {
                    WriteRing(writer, ring);
                }

                writer.WriteEndArray();
                break;
        }

        writer.WriteEndObject();
    }

    private void WriteRing(Utf8JsonWriter writer, IReadOnlyList<GeoPoint> points)
    {
        writer.WriteStartArray();
        foreach (var point in points)
        {
            WritePoint(writer, point);
        }

        writer.WriteEndArray();
    }

    private void WritePoint(Utf8JsonWriter writer, GeoPoint point)
    {
        writer.WriteStartArray();
        writer.WriteNumberValue(Round(point.X));
        writer.WriteNumberValue(Round(point.Y));
        writer.WriteEndArray();
    }

    private double Round(double value) => Math.Round(value, decimals);

    /// <summary>A small SVG drawn by this code from the numbers alone (no text), rasterized to PNG by libvips.</summary>
    private async Task WriteThumbnailAsync(PreviewWorkContext context, CancellationToken cancellationToken)
    {
        if (!PdfPageRenderer.Installed(context.Tools.Vips))
        {
            return;
        }

        const double size = 512;
        var scaleX = Geographic ? Math.Cos((minY + maxY) / 2 * Math.PI / 180) : 1;
        var width = Math.Max((maxX - minX) * scaleX, 1e-9);
        var height = Math.Max(maxY - minY, 1e-9);
        var scale = (size - 32) / Math.Max(width, height);
        string X(double x) => (16 + (x - minX) * scaleX * scale).ToString("0.##", CultureInfo.InvariantCulture);
        string Y(double y) => (size - 16 - (y - minY) * scale).ToString("0.##", CultureInfo.InvariantCulture);

        var svg = new StringBuilder();
        svg.Append(CultureInfo.InvariantCulture, $"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"{size}\" height=\"{size}\" viewBox=\"0 0 {size} {size}\">");
        svg.Append("<rect width=\"100%\" height=\"100%\" fill=\"#10151d\"/>");
        var drawn = 0;
        foreach (var feature in features)
        {
            if (drawn++ > 5000)
            {
                break;
            }

            foreach (var part in feature.Parts)
            {
                if (feature.Kind is "point" or "text")
                {
                    svg.Append(CultureInfo.InvariantCulture, $"<circle cx=\"{X(part[0].X)}\" cy=\"{Y(part[0].Y)}\" r=\"3\" fill=\"#f5b942\"/>");
                    continue;
                }

                svg.Append(feature.Kind == "polygon" ? "<polygon fill=\"#4aa3ff33\" stroke=\"#4aa3ff\" stroke-width=\"1.5\" points=\"" : "<polyline fill=\"none\" stroke=\"#7ad1a8\" stroke-width=\"1.5\" points=\"");
                foreach (var point in part.Take(2000))
                {
                    svg.Append(X(point.X)).Append(',').Append(Y(point.Y)).Append(' ');
                }

                svg.Append("\"/>");
            }
        }

        svg.Append("</svg>");
        var svgPath = Path.Combine(context.WorkDirectory, "thumbnail-source.svg");
        await File.WriteAllTextAsync(svgPath, svg.ToString(), cancellationToken);
        try
        {
            await RasterImagePreviewProcessor.ThumbnailAsync(context, svgPath, PreviewArtifactFiles.Thumbnail, context.Limits.ThumbnailPixels, "[strip]", cancellationToken);
            var thumbnail = RasterHeader.Read(context.OutputPath(PreviewArtifactFiles.Thumbnail));
            context.Record(PreviewArtifactFiles.Thumbnail, PreviewArtifactKind.Thumbnail, "image/png", width: thumbnail?.Width, height: thumbnail?.Height);
        }
        catch (PreviewFailureException)
        {
            // A thumbnail is a convenience; the geometry document is the preview. An unrecorded file is never ingested.
        }
    }

    /// <summary>Plain, bounded text: control and bidi characters removed, markup left as literal characters.</summary>
    public static string? Clean(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var builder = new StringBuilder(Math.Min(value.Length, MaxTextLength));
        foreach (var rune in value.EnumerateRunes())
        {
            if (builder.Length >= MaxTextLength)
            {
                break;
            }

            var category = Rune.GetUnicodeCategory(rune);
            if (category is UnicodeCategory.Control or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator)
            {
                builder.Append(' ');
            }
            else if (category is not (UnicodeCategory.Format or UnicodeCategory.Surrogate or UnicodeCategory.PrivateUse or UnicodeCategory.OtherNotAssigned))
            {
                builder.Append(rune.ToString());
            }
        }

        var collapsed = string.Join(' ', builder.ToString().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return collapsed.Length == 0 ? null : collapsed;
    }

    private sealed record Feature(
        string Kind,
        IReadOnlyList<IReadOnlyList<GeoPoint>> Parts,
        string? Name,
        string? Description,
        string? Group,
        string? Text,
        double TextHeight,
        double Rotation);
}
