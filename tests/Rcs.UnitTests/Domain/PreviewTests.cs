using System.IO.Compression;
using System.Text;
using Microsoft.Extensions.Options;
using Rcs.Application.Previews;
using Rcs.Domain.Documents;
using Rcs.Infrastructure.Configuration;
using Rcs.Infrastructure.Previews;
using Rcs.PreviewWorker.Processors;
using Rcs.PreviewWorker.Processing;

namespace Rcs.UnitTests.Domain;

public sealed class PreviewTests
{
    [Fact]
    public void KmzEnforcesActualReadLimitAndCompressionRatio()
    {
        using var limited = new LimitedReadStream(new MemoryStream(new byte[100]), 10);
        Assert.Equal(PreviewFailureCodes.ArchiveTooLarge, Assert.Throws<PreviewFailureException>(() => limited.Read(new byte[100])).Code);
        using var bytes = new MemoryStream();
        using (var zip = new ZipArchive(bytes, ZipArchiveMode.Create, true))
        using (var writer = new StreamWriter(zip.CreateEntry("doc.kml", CompressionLevel.SmallestSize).Open())) writer.Write(new string('a', 2 * 1024 * 1024));
        bytes.Position = 0;
        using var archive = new ZipArchive(bytes, ZipArchiveMode.Read);
        Assert.Equal(PreviewFailureCodes.ArchiveUnsafe, Assert.Throws<PreviewFailureException>(() => KmzPreviewProcessor.SelectKml(archive, new())).Code);
        Assert.Equal(PreviewFailureCodes.ArchiveTooLarge, Assert.Throws<PreviewFailureException>(() => KmzPreviewProcessor.SelectKml(archive, new PreviewLimits { KmzMaxEntries = 0 })).Code);
    }

    [Fact]
    public void GeometryManifestRejectsNonNumericCoordinatesAndUnknownShapes()
    {
        using var good = System.Text.Json.JsonDocument.Parse("{\"format\":\"rcs-geometry/1\",\"bounds\":[0,0,1,1],\"features\":[{\"t\":\"point\",\"c\":[0,1]}]}");
        using var bad = System.Text.Json.JsonDocument.Parse("{\"format\":\"rcs-geometry/1\",\"bounds\":[0,0,1,1],\"features\":[{\"t\":\"point\",\"c\":[\"NaN\",1]}]}");
        Assert.True(PreviewManifestValidation.IsGeometryValid(good.RootElement, new()));
        Assert.False(PreviewManifestValidation.IsGeometryValid(bad.RootElement, new()));
        Assert.False(PreviewManifestValidation.IsGeometryValid(good.RootElement, new PreviewLimits { MaxCoordinates = 0 }));
    }

    [Fact]
    public void ManifestRejectsNullArtifactsDuplicateSlotsAndInvalidPageCounts()
    {
        var valid = new PreviewManifest("READY", null, null, "PAGES", 2, 1,
            [new("page-0001.png", "PAGE", "image/png", 1, 100, 200)]);
        Assert.True(PreviewManifestValidation.IsValid(valid, PreviewType.Pages, new()));
        Assert.False(PreviewManifestValidation.IsValid(valid with { Artifacts = null! }, PreviewType.Pages, new()));
        Assert.False(PreviewManifestValidation.IsValid(valid with { Artifacts = [null!] }, PreviewType.Pages, new()));
        Assert.False(PreviewManifestValidation.IsValid(valid with { PageCount = 0 }, PreviewType.Pages, new()));
        Assert.False(PreviewManifestValidation.IsValid(valid with { PagesRendered = 3 }, PreviewType.Pages, new()));
        Assert.False(PreviewManifestValidation.IsValid(valid with { PreviewType = "IMAGE" }, PreviewType.Pages, new()));
        Assert.False(PreviewManifestValidation.IsValid(valid with { Artifacts = [valid.Artifacts[0], valid.Artifacts[0]] }, PreviewType.Pages, new()));
        Assert.False(PreviewManifestValidation.IsValid(valid with { Artifacts = [valid.Artifacts[0] with { Width = 50000 }] }, PreviewType.Pages, new()));
        Assert.False(PreviewManifestValidation.IsValid(valid with { Artifacts = [valid.Artifacts[0] with { File = "../page-0001.png" }] }, PreviewType.Pages, new()));
        Assert.False(PreviewManifestValidation.IsValid(valid with { Status = "FAILED", FailureCode = "MALFORMED" }, PreviewType.Pages, new()));
    }

    [Theory]
    [InlineData("TIMEOUT", true)]
    [InlineData("WORKER_FAILED", true)]
    [InlineData("MALFORMED", false)]
    [InlineData("ARCHIVE_UNSAFE", false)]
    [InlineData("ENCRYPTED", false)]
    public void OnlyTransientFailuresAreAutomaticallyRetried(string code, bool retry) => Assert.Equal(retry, PreviewFailureCodes.IsTransient(code));

    [Fact]
    public void DxfProducesBoundedGeometry()
    {
        using var stream = new MemoryStream(Encoding.ASCII.GetBytes("0\nSECTION\n2\nENTITIES\n0\nLINE\n10\n0\n20\n0\n11\n10\n21\n20\n0\nENDSEC\n0\nEOF\n"));
        var limits = new PreviewLimits();
        var drawing = DxfPreviewProcessor.Parse(stream, limits);
        var geometry = new GeometryBuilder(limits, false);
        DxfPreviewProcessor.Emit(drawing, geometry, limits);
        Assert.Equal(1, geometry.FeatureCount);
        Assert.Equal(2, geometry.CoordinateCount);
    }

    [Theory]
    [InlineData("../doc.kml")]
    [InlineData("/doc.kml")]
    [InlineData("a/../../doc.kml")]
    [InlineData("C:/doc.kml")]
    [InlineData("a\\doc.kml")]
    [InlineData("a/./doc.kml")]
    [InlineData("a\u0000/doc.kml")]
    public void KmzRejectsUnsafeNames(string name) => Assert.False(KmzPreviewProcessor.IsSafeEntryName(name));

    [Theory]
    [InlineData("doc.kml")]
    [InlineData("folder/ərazi.kml")]
    public void KmzAcceptsRelativeNames(string name) => Assert.True(KmzPreviewProcessor.IsSafeEntryName(name));

    [Fact]
    public void KmzEnforcesDecompressedSizeBeforeOpeningEntry()
    {
        using var bytes = new MemoryStream();
        using (var zip = new ZipArchive(bytes, ZipArchiveMode.Create, true))
        using (var writer = new StreamWriter(zip.CreateEntry("doc.kml").Open())) writer.Write(new string('a', 100));
        bytes.Position = 0;
        using var archive = new ZipArchive(bytes, ZipArchiveMode.Read);
        Assert.Equal(PreviewFailureCodes.ArchiveTooLarge, Assert.Throws<PreviewFailureException>(() =>
            KmzPreviewProcessor.SelectKml(archive, new PreviewLimits { KmzMaxUncompressedBytes = 20 })).Code);
    }

    [Fact]
    public void KmlRefusesExternalEntities()
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("<!DOCTYPE kml [<!ENTITY e SYSTEM 'file:///etc/passwd'>]><kml>&e;</kml>"));
        Assert.Throws<System.Xml.XmlException>(() => KmzPreviewProcessor.Read(stream, new GeometryBuilder(new(), true), new()));
    }

    [Fact]
    public void KmlReadsPointLineAndPolygon()
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("<kml><Document><Placemark><Point><coordinates>49,40</coordinates></Point></Placemark><Placemark><LineString><coordinates>49,40 50,41</coordinates></LineString></Placemark><Placemark><Polygon><outerBoundaryIs><LinearRing><coordinates>49,40 50,40 50,41 49,40</coordinates></LinearRing></outerBoundaryIs></Polygon></Placemark></Document></kml>"));
        var geometry = new GeometryBuilder(new(), true);
        KmzPreviewProcessor.Read(stream, geometry, new());
        Assert.Equal(3, geometry.FeatureCount);
    }

    [Theory]
    [InlineData("../page-0001.png")]
    [InlineData("image.svg")]
    [InlineData("image.html")]
    [InlineData("/image.png")]
    public void WorkerArtifactNamesAreClosed(string name) => Assert.False(PreviewArtifactFiles.IsValid(name));

    [Fact]
    public void PreviewRootsCannotOverlapOriginalsOrEachOther()
    {
        var root = Path.Combine(Path.GetTempPath(), "rcs-preview-path-test");
        var storage = Options.Create(new StorageOptions { RootPath = Path.Combine(root, "objects"), TempPath = Path.Combine(root, "uploads") });
        Assert.Throws<InvalidOperationException>(() => new PreviewPaths(Options.Create(new PreviewOptions { StorageRoot = root, TempRoot = Path.Combine(root, "tmp") }), storage));
        Assert.Throws<InvalidOperationException>(() => new PreviewPaths(Options.Create(new PreviewOptions { StorageRoot = Path.Combine(root, "preview"), TempRoot = Path.Combine(root, "preview", "tmp") }), storage));
    }

    [Fact]
    public void CleanupRefusesParentAndOriginalPaths()
    {
        var root = Path.Combine(Path.GetTempPath(), "rcs-preview-path-test");
        var paths = new PreviewPaths(Options.Create(new PreviewOptions { StorageRoot = Path.Combine(root, "preview"), TempRoot = Path.Combine(root, "jobs") }), Options.Create(new StorageOptions()));
        Assert.Throws<InvalidOperationException>(() => paths.DeleteJobDirectory(root));
        Assert.Throws<InvalidOperationException>(() => paths.DeleteJobDirectory(Path.Combine(root, "objects")));
    }

    [Theory]
    [InlineData("application/pdf", true)]
    [InlineData("image/png", true)]
    [InlineData("image/jpeg", true)]
    [InlineData("image/webp", true)]
    [InlineData("application/vnd.openxmlformats-officedocument.wordprocessingml.document", true)]
    [InlineData("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", true)]
    [InlineData("application/octet-stream", false)]
    [InlineData("application/vnd.ms-word.document.macroEnabled.12", false)]
    public void RoutesReflectActualSupport(string mime, bool supported) => Assert.Equal(supported, PreviewRouting.Route(mime).IsSupported);
}
