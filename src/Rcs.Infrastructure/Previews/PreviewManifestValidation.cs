using Rcs.Application.Previews;
using Rcs.Domain.Documents;
using Rcs.Domain.Vocabulary;

namespace Rcs.Infrastructure.Previews;

internal static class PreviewManifestValidation
{
    internal static bool IsGeometryValid(System.Text.Json.JsonElement root, PreviewLimits limits)
    {
        if (root.ValueKind != System.Text.Json.JsonValueKind.Object
            || !root.TryGetProperty("format", out var format) || format.ValueKind != System.Text.Json.JsonValueKind.String || !format.ValueEquals("rcs-geometry/1")
            || !root.TryGetProperty("bounds", out var bounds) || bounds.ValueKind != System.Text.Json.JsonValueKind.Array || bounds.GetArrayLength() != 4
            || !root.TryGetProperty("features", out var features) || features.ValueKind != System.Text.Json.JsonValueKind.Array
            || features.GetArrayLength() is < 1 || features.GetArrayLength() > limits.MaxFeatures) return false;
        if (bounds.EnumerateArray().Any(v => v.ValueKind != System.Text.Json.JsonValueKind.Number || !v.TryGetDouble(out var number) || !double.IsFinite(number))) return false;
        var coordinates = 0;
        bool Point(System.Text.Json.JsonElement point)
        {
            if (point.ValueKind != System.Text.Json.JsonValueKind.Array || point.GetArrayLength() != 2 || ++coordinates > limits.MaxCoordinates) return false;
            return point.EnumerateArray().All(v => v.ValueKind == System.Text.Json.JsonValueKind.Number && v.TryGetDouble(out var number) && double.IsFinite(number));
        }
        bool Ring(System.Text.Json.JsonElement ring) => ring.ValueKind == System.Text.Json.JsonValueKind.Array && ring.GetArrayLength() >= 2 && ring.EnumerateArray().All(Point);
        foreach (var feature in features.EnumerateArray())
        {
            if (feature.ValueKind != System.Text.Json.JsonValueKind.Object || !feature.TryGetProperty("t", out var type)
                || type.ValueKind != System.Text.Json.JsonValueKind.String || !feature.TryGetProperty("c", out var points)) return false;
            foreach (var name in new[] { "n", "d", "s" })
            {
                if (feature.TryGetProperty(name, out var label) && (label.ValueKind != System.Text.Json.JsonValueKind.String || label.GetString()!.Length > 500)) return false;
            }
            var valid = type.GetString() switch
            {
                "point" or "text" => Point(points),
                "line" => Ring(points),
                "polygon" => points.ValueKind == System.Text.Json.JsonValueKind.Array && points.GetArrayLength() > 0 && points.EnumerateArray().All(Ring),
                _ => false
            };
            if (!valid) return false;
        }
        return bounds[0].GetDouble() <= bounds[2].GetDouble() && bounds[1].GetDouble() <= bounds[3].GetDouble();
    }

    internal static bool IsValid(PreviewManifest manifest, PreviewType expected, PreviewLimits limits)
    {
        if (manifest.Artifacts is null || manifest.Artifacts.Any(a => a is null)) return false;
        if (manifest.Status is "FAILED" or "UNSUPPORTED")
            return manifest.Artifacts.Count == 0 && manifest.FailureCode is not null && PreviewFailureCodes.All.Contains(manifest.FailureCode);
        if (manifest.Status != "READY" || manifest.PreviewType != expected.ToCode() || manifest.FailureCode is not null
            || manifest.Artifacts.Count == 0 || manifest.Artifacts.Count > limits.MaxPages + 3) return false;
        if (manifest.Artifacts.Select(a => a.File).Distinct(StringComparer.Ordinal).Count() != manifest.Artifacts.Count) return false;
        if (expected == PreviewType.Pages && (manifest.PageCount is not > 0 || manifest.PagesRendered is not > 0
            || manifest.PagesRendered > manifest.PageCount || manifest.PagesRendered > limits.MaxPages)) return false;
        if (expected != PreviewType.Pages && (manifest.PageCount is not null || manifest.PagesRendered is not null)) return false;
        foreach (var artifact in manifest.Artifacts)
        {
            if (!PreviewArtifactFiles.IsValid(artifact.File)) return false;
            if (artifact.Kind == "GEOMETRY")
            {
                if (artifact.Width is not null || artifact.Height is not null || artifact.PageNumber is not null) return false;
            }
            else if (artifact.Width is not > 0 || artifact.Height is not > 0
                || artifact.Width > limits.MaxPreviewPixels || artifact.Height > limits.MaxPreviewPixels) return false;
        }
        return manifest.Artifacts.GroupBy(a => (a.Kind, a.PageNumber)).All(group => group.Count() == 1);
    }
}
