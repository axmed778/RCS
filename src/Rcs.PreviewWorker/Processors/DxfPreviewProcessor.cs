using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Rcs.Application.Previews;
using Rcs.Domain.Documents;
using Rcs.PreviewWorker.Processing;

namespace Rcs.PreviewWorker.Processors;

/// <summary>
/// ASCII DXF → a geometry document of its model-space drawing: lines, polylines, circles, arcs, ellipses, splines (as
/// their fit or control polygon), solids, points, text, and block references (INSERT, DIMENSION) expanded with their
/// transforms up to a fixed nesting depth. DXF is a documented text format of group-code/value pairs; this reader
/// extracts geometry only, with hard limits on line length, entity size, nesting and total work. Binary DXF and DWG are
/// not read here (ADR-044: no DWG parser inside RCS).
/// </summary>
public sealed partial class DxfPreviewProcessor : IDocumentPreviewProcessor
{
    private const int MaxLineLength = 4096;
    private const int MaxBlockDepth = 8;
    private static readonly byte[] BinaryMagic = "AutoCAD Binary DXF"u8.ToArray();

    public string ProcessorName => PreviewRouting.DxfProcessor;

    public string ImplementationVersion => "1";

    public bool CanProcess(string contentType) => PreviewRouting.Route(contentType).Processor == ProcessorName;

    public Task<PreviewProcessorCapability> ProbeAsync(PreviewToolPaths tools, CancellationToken cancellationToken) =>
        Task.FromResult(new PreviewProcessorCapability(ProcessorName, $"{ImplementationVersion};dxf-reader=1", true, null));

    public async Task<ProcessorOutcome> GenerateAsync(PreviewWorkContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        context.RequireInputAtMost(context.Limits.MaxInputBytes);
        await using var stream = new FileStream(context.InputPath, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.SequentialScan);
        var head = new byte[BinaryMagic.Length];
        var headLength = await stream.ReadAtLeastAsync(head, head.Length, throwOnEndOfStream: false, cancellationToken);
        if (headLength == head.Length && head.AsSpan().SequenceEqual(BinaryMagic))
        {
            throw new PreviewFailureException(PreviewFailureCodes.FormatNotSupported, "Binary DXF is not supported by the preview reader.", unsupported: true);
        }

        stream.Position = 0;
        var geometry = new GeometryBuilder(context.Limits, geographic: false);
        var drawing = Parse(stream, context.Limits);
        Emit(drawing, geometry, context.Limits);
        await geometry.WriteAsync(context, "dxf", drawing.Units, cancellationToken);
        return new ProcessorOutcome(PreviewType.Geometry);
    }

    // ------------------------------------------------------------------ parsing

    /// <summary>One entity: its type and its group-code/value pairs, in file order.</summary>
    public sealed record DxfEntity(string Type, List<(int Code, string Value)> Pairs)
    {
        public string? Get(int code) => Pairs.FirstOrDefault(pair => pair.Code == code).Value;

        public double Number(int code, double fallback = 0) =>
            Get(code) is { } text && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : fallback;

        public IEnumerable<double> Numbers(int code) =>
            Pairs.Where(pair => pair.Code == code)
                .Select(pair => double.TryParse(pair.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : double.NaN);
    }

    public sealed record DxfBlock(string Name, GeoPoint Base, List<DxfEntity> Entities);

    public sealed record DxfDrawing(string? Units, List<DxfEntity> Entities, Dictionary<string, DxfBlock> Blocks);

    public static DxfDrawing Parse(Stream stream, PreviewLimits limits)
    {
        ArgumentNullException.ThrowIfNull(limits);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, bufferSize: 64 * 1024, leaveOpen: true);
        var entities = new List<DxfEntity>();
        var blocks = new Dictionary<string, DxfBlock>(StringComparer.OrdinalIgnoreCase);
        string? units = null;
        string? section = null;
        DxfEntity? current = null;
        List<DxfEntity>? target = null;
        (string Name, double X, double Y, List<DxfEntity> Entities)? block = null;
        long pairs = 0;
        var maxPairs = (long)limits.MaxCoordinates * 8;
        var expectSection = false;
        var expectUnits = false;

        void Finish()
        {
            if (current is not null && target is not null)
            {
                target.Add(current);
            }

            current = null;
        }

        while (ReadPair(reader) is { } pair)
        {
            if (++pairs > maxPairs)
            {
                throw new PreviewFailureException(PreviewFailureCodes.InputTooLarge, "The drawing is too large to preview.");
            }

            var (code, value) = pair;
            if (expectSection)
            {
                expectSection = false;
                if (code == 2)
                {
                    section = value.ToUpperInvariant();
                    continue;
                }
            }

            if (section == "HEADER")
            {
                if (code == 9)
                {
                    expectUnits = value == "$INSUNITS";
                }
                else if (code == 70 && expectUnits)
                {
                    units = UnitName(value);
                    expectUnits = false;
                }
            }

            if (code != 0)
            {
                if (current is not null)
                {
                    if (current.Pairs.Count >= limits.MaxCoordinates)
                    {
                        throw new PreviewFailureException(PreviewFailureCodes.InputTooLarge, "A drawing entity is too large to preview.");
                    }

                    current.Pairs.Add((code, value));
                }
                else if (block is { } open && code is 2 or 10 or 20)
                {
                    block = code switch
                    {
                        2 => open with { Name = value },
                        10 => open with { X = ParseNumber(value) },
                        _ => open with { Y = ParseNumber(value) },
                    };
                }

                continue;
            }

            Finish();
            switch (value)
            {
                case "SECTION":
                    expectSection = true;
                    break;
                case "ENDSEC":
                    section = null;
                    target = null;
                    break;
                case "EOF":
                    return new DxfDrawing(units, entities, blocks);
                case "BLOCK" when section == "BLOCKS":
                    block = (string.Empty, 0, 0, []);
                    target = null;
                    break;
                case "ENDBLK" when section == "BLOCKS":
                    if (block is { Name.Length: > 0 } done && blocks.Count < 50_000)
                    {
                        blocks[done.Name] = new DxfBlock(done.Name, new GeoPoint(done.X, done.Y), done.Entities);
                    }

                    block = null;
                    target = null;
                    break;
                default:
                    if (section == "ENTITIES")
                    {
                        target = entities;
                        current = new DxfEntity(value, []);
                    }
                    else if (section == "BLOCKS" && block is { } inBlock)
                    {
                        target = inBlock.Entities;
                        current = new DxfEntity(value, []);
                    }

                    break;
            }
        }

        Finish();
        return new DxfDrawing(units, entities, blocks);
    }

    private static (int Code, string Value)? ReadPair(StreamReader reader)
    {
        var codeLine = ReadBoundedLine(reader);
        if (codeLine is null)
        {
            return null;
        }

        var valueLine = ReadBoundedLine(reader) ?? string.Empty;
        return int.TryParse(codeLine.Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var code)
            ? (code, valueLine.Trim())
            : throw new PreviewFailureException(PreviewFailureCodes.Malformed, "The DXF group codes are not readable.");
    }

    private static string? ReadBoundedLine(StreamReader reader)
    {
        var builder = new StringBuilder();
        int next;
        while ((next = reader.Read()) >= 0)
        {
            if (next == '\n')
            {
                return builder.ToString().TrimEnd('\r');
            }

            if (builder.Length >= MaxLineLength)
            {
                throw new PreviewFailureException(PreviewFailureCodes.Malformed, "The DXF contains an over-long line.");
            }

            builder.Append((char)next);
        }

        return builder.Length == 0 ? null : builder.ToString().TrimEnd('\r');
    }

    // ------------------------------------------------------------------ geometry

    /// <summary>A 2-D affine transform: x' = A·x + C·y + E, y' = B·x + D·y + F.</summary>
    private readonly record struct Affine(double A, double B, double C, double D, double E, double F)
    {
        public static readonly Affine Identity = new(1, 0, 0, 1, 0, 0);

        public GeoPoint Apply(GeoPoint point) => new(A * point.X + C * point.Y + E, B * point.X + D * point.Y + F);

        public Affine Then(Affine outer) => new(
            outer.A * A + outer.C * B,
            outer.B * A + outer.D * B,
            outer.A * C + outer.C * D,
            outer.B * C + outer.D * D,
            outer.A * E + outer.C * F + outer.E,
            outer.B * E + outer.D * F + outer.F);

        public double Scale => Math.Sqrt(Math.Abs(A * D - B * C));

        public double RotationDegrees => Math.Atan2(B, A) * 180 / Math.PI;
    }

    public static void Emit(DxfDrawing drawing, GeometryBuilder geometry, PreviewLimits limits)
    {
        ArgumentNullException.ThrowIfNull(drawing);
        ArgumentNullException.ThrowIfNull(geometry);
        ArgumentNullException.ThrowIfNull(limits);
        var budget = (long)limits.MaxCoordinates * 4;
        EmitAll(drawing.Entities, drawing, geometry, Affine.Identity, 0, ref budget, null);
    }

    private static void EmitAll(IReadOnlyList<DxfEntity> source, DxfDrawing drawing, GeometryBuilder geometry, Affine transform, int depth, ref long budget, string? inheritedLayer)
    {
        for (var index = 0; index < source.Count && !geometry.IsFull; index++)
        {
            if (--budget < 0)
            {
                // A block that references itself many times over (a "block bomb") ends here, not in memory exhaustion.
                geometry.Skipped++;
                return;
            }

            var entity = source[index];
            if (entity.Get(67) == "1")
            {
                continue; // paper space
            }

            var layer = entity.Get(8) is { Length: > 0 } own && own != "0" ? own : inheritedLayer ?? entity.Get(8);
            switch (entity.Type)
            {
                case "LINE":
                    Line(geometry, transform, layer, [Point(entity, 10, 20), Point(entity, 11, 21)]);
                    break;
                case "LWPOLYLINE":
                    var vertices = entity.Numbers(10).Zip(entity.Numbers(20), (x, y) => new GeoPoint(x, y)).ToList();
                    if (((int)entity.Number(70) & 1) == 1 && vertices.Count > 2)
                    {
                        vertices.Add(vertices[0]);
                    }

                    Line(geometry, transform, layer, vertices);
                    break;
                case "POLYLINE":
                    var closed = ((int)entity.Number(70) & 1) == 1;
                    var points = new List<GeoPoint>();
                    while (index + 1 < source.Count && source[index + 1].Type == "VERTEX")
                    {
                        points.Add(Point(source[++index], 10, 20));
                    }

                    if (index + 1 < source.Count && source[index + 1].Type == "SEQEND")
                    {
                        index++;
                    }

                    if (closed && points.Count > 2)
                    {
                        points.Add(points[0]);
                    }

                    Line(geometry, transform, layer, points);
                    break;
                case "CIRCLE":
                    Line(geometry, transform, layer, Arc(Point(entity, 10, 20), entity.Number(40), 0, 360));
                    break;
                case "ARC":
                    Line(geometry, transform, layer, Arc(Point(entity, 10, 20), entity.Number(40), entity.Number(50), entity.Number(51)));
                    break;
                case "ELLIPSE":
                    Line(geometry, transform, layer, Ellipse(entity));
                    break;
                case "SPLINE":
                    var fit = entity.Numbers(11).Zip(entity.Numbers(21), (x, y) => new GeoPoint(x, y)).ToList();
                    Line(geometry, transform, layer, fit.Count >= 2 ? fit : entity.Numbers(10).Zip(entity.Numbers(20), (x, y) => new GeoPoint(x, y)).ToList());
                    break;
                case "SOLID" or "TRACE" or "3DFACE":
                    var corners = entity.Type == "3DFACE"
                        ? new[] { Point(entity, 10, 20), Point(entity, 11, 21), Point(entity, 12, 22), Point(entity, 13, 23) }
                        : [Point(entity, 10, 20), Point(entity, 11, 21), Point(entity, 13, 23), Point(entity, 12, 22)];
                    geometry.AddPolygon([corners.Select(transform.Apply).ToArray()], null, null, layer);
                    break;
                case "POINT":
                    geometry.AddPoint(transform.Apply(Point(entity, 10, 20)), null, null, layer);
                    break;
                case "TEXT" or "ATTRIB":
                    Text(geometry, transform, layer, Point(entity, 10, 20), entity.Get(1), entity.Number(40, 1), entity.Number(50));
                    break;
                case "MTEXT":
                    var chunks = string.Concat(entity.Pairs.Where(pair => pair.Code == 3).Select(pair => pair.Value)) + entity.Get(1);
                    Text(geometry, transform, layer, Point(entity, 10, 20), CleanMText(chunks), entity.Number(40, 1), entity.Number(50));
                    break;
                case "INSERT" or "DIMENSION":
                    if (depth >= MaxBlockDepth || entity.Get(2) is not { Length: > 0 } name || !drawing.Blocks.TryGetValue(name, out var block))
                    {
                        geometry.Skipped++;
                        break;
                    }

                    // A DIMENSION's anonymous block is already in drawing coordinates.
                    var local = entity.Type == "DIMENSION" ? Affine.Identity : InsertTransform(entity, block);
                    EmitAll(block.Entities, drawing, geometry, local.Then(transform), depth + 1, ref budget, layer);
                    break;
                default:
                    geometry.Skipped++;
                    break;
            }
        }
    }

    private static Affine InsertTransform(DxfEntity insert, DxfBlock block)
    {
        var scaleX = insert.Number(41, 1);
        var scaleY = insert.Number(42, 1);
        var angle = insert.Number(50) * Math.PI / 180;
        var (sin, cos) = Math.SinCos(angle);
        var at = Point(insert, 10, 20);
        var toBase = new Affine(1, 0, 0, 1, -block.Base.X, -block.Base.Y);
        var scaled = new Affine(scaleX, 0, 0, scaleY, 0, 0);
        var rotated = new Affine(cos, sin, -sin, cos, at.X, at.Y);
        return toBase.Then(scaled).Then(rotated);
    }

    private static void Line(GeometryBuilder geometry, Affine transform, string? layer, IReadOnlyList<GeoPoint> points) =>
        geometry.AddLine(points.Select(transform.Apply).ToArray(), null, null, layer);

    private static void Text(GeometryBuilder geometry, Affine transform, string? layer, GeoPoint at, string? text, double height, double rotation)
    {
        if (text is { Length: > 0 })
        {
            geometry.AddText(transform.Apply(at), text, height * transform.Scale, rotation + transform.RotationDegrees, layer);
        }
    }

    private static GeoPoint Point(DxfEntity entity, int xCode, int yCode) => new(entity.Number(xCode), entity.Number(yCode));

    private static List<GeoPoint> Arc(GeoPoint center, double radius, double startDegrees, double endDegrees)
    {
        if (!(radius > 0))
        {
            return [];
        }

        var sweep = endDegrees - startDegrees;
        while (sweep <= 0)
        {
            sweep += 360;
        }

        sweep = Math.Min(sweep, 360);
        var segments = Math.Max(8, (int)Math.Ceiling(sweep / 360 * 72));
        var points = new List<GeoPoint>(segments + 1);
        for (var step = 0; step <= segments; step++)
        {
            var angle = (startDegrees + sweep * step / segments) * Math.PI / 180;
            points.Add(new GeoPoint(center.X + radius * Math.Cos(angle), center.Y + radius * Math.Sin(angle)));
        }

        return points;
    }

    private static List<GeoPoint> Ellipse(DxfEntity entity)
    {
        var center = Point(entity, 10, 20);
        var major = Point(entity, 11, 21);
        var ratio = entity.Number(40, 1);
        var start = entity.Number(41);
        var end = entity.Number(42, 2 * Math.PI);
        if (end <= start)
        {
            end += 2 * Math.PI;
        }

        var minor = new GeoPoint(-major.Y * ratio, major.X * ratio);
        const int segments = 72;
        var points = new List<GeoPoint>(segments + 1);
        for (var step = 0; step <= segments; step++)
        {
            var t = start + (end - start) * step / segments;
            points.Add(new GeoPoint(center.X + major.X * Math.Cos(t) + minor.X * Math.Sin(t), center.Y + major.Y * Math.Cos(t) + minor.Y * Math.Sin(t)));
        }

        return points;
    }

    /// <summary>Removes MTEXT inline formatting (<c>\P</c>, <c>\fArial;</c>, braces) and keeps the words.</summary>
    public static string CleanMText(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var withoutCodes = MTextCodePattern().Replace(text.Replace("\\P", " ", StringComparison.Ordinal), string.Empty);
        return withoutCodes.Replace("{", string.Empty, StringComparison.Ordinal).Replace("}", string.Empty, StringComparison.Ordinal);
    }

    private static double ParseNumber(string value) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) ? number : 0;

    private static string? UnitName(string code) => code.Trim() switch
    {
        "1" => "in",
        "2" => "ft",
        "3" => "mi",
        "4" => "mm",
        "5" => "cm",
        "6" => "m",
        "7" => "km",
        _ => null,
    };

    [GeneratedRegex(@"\\[A-Za-z][^;\\{}]{0,200};|\\[A-Za-z~]")]
    private static partial Regex MTextCodePattern();
}
