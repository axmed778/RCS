using Rcs.Domain.Documents;
using Rcs.Domain.Vocabulary;

namespace Rcs.UnitTests.Domain;

public sealed class DocumentRuleTests
{
    [Theory]
    [InlineData(DocumentTargetKind.Correspondence, DocumentLinkRoleCodes.PrimaryLetter, true)]
    [InlineData(DocumentTargetKind.Case, DocumentLinkRoleCodes.PrimaryLetter, false)]
    [InlineData(DocumentTargetKind.Request, DocumentLinkRoleCodes.Attachment, false)]
    [InlineData(DocumentTargetKind.Requirement, DocumentLinkRoleCodes.RequirementEvidence, true)]
    [InlineData(DocumentTargetKind.FinalResult, DocumentLinkRoleCodes.FinalResultDocument, true)]
    public void OfficialFilesStayInTheirBusinessContext(DocumentTargetKind target, string role, bool allowed) =>
        Assert.Equal(allowed, DocumentRules.RoleFitsTarget(target, role));

    [Fact]
    public void HistoricalAndCrossCasePlacementsNeverFloat()
    {
        Assert.False(DocumentRules.MayFloat(DocumentTargetKind.Correspondence, DocumentLinkRoleCodes.Supporting, true));
        Assert.False(DocumentRules.MayFloat(DocumentTargetKind.Requirement, DocumentLinkRoleCodes.RequirementEvidence, true));
        Assert.False(DocumentRules.MayFloat(DocumentTargetKind.Case, DocumentLinkRoleCodes.Supporting, false));
        Assert.True(DocumentRules.MayFloat(DocumentTargetKind.Case, DocumentLinkRoleCodes.Supporting, true));
    }

    [Fact]
    public void ReinstatementRequiresReasonAndNewestNonWithdrawnVersion()
    {
        var old = Guid.NewGuid(); var latest = Guid.NewGuid();
        VersionFacts[] versions = [new(old, 1, DocumentVersionStatus.Superseded), new(latest, 2, DocumentVersionStatus.Withdrawn)];
        Assert.False(DocumentRules.CanReinstate(versions, old, " ").IsAllowed);
        Assert.False(DocumentRules.CanReinstate(versions, latest, "reason").IsAllowed);
        Assert.True(DocumentRules.CanReinstate(versions, old, "reason").IsAllowed);
    }

    [Fact]
    public void DetectedContentWinsOverMisleadingExtensionAndHostileName()
    {
        var detected = FileTypePolicy.Detect("%PDF-1.7"u8, "map.exe");
        Assert.Equal("application/pdf", detected.MimeType);
        Assert.False(detected.ExtensionMatchesContent);
        var safe = FileTypePolicy.SafeDownloadFileName("../ərazi\u202e\r\n/map", "map.exe", detected.MimeType);
        Assert.EndsWith(".pdf", safe, StringComparison.Ordinal);
        Assert.DoesNotContain("/", safe, StringComparison.Ordinal);
        Assert.DoesNotContain("\u202e", safe, StringComparison.Ordinal);
        Assert.DoesNotContain("\r", safe, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("map.kmz", FileFamily.Kmz, false)]
    [InlineData("letter.docx", FileFamily.Word, false)]
    [InlineData("letter.docm", FileFamily.Word, true)]
    [InlineData("table.xlsx", FileFamily.Excel, false)]
    [InlineData("table.xlsm", FileFamily.Excel, true)]
    public void ContainersAreClassifiedWithoutOpeningThem(string name, FileFamily family, bool macro)
    {
        var detected = FileTypePolicy.Detect([0x50, 0x4b, 0x03, 0x04], name);
        Assert.Equal(family, detected.Family);
        Assert.Equal(macro, detected.IsMacroEnabled);
    }

    [Fact]
    public void UnconfirmedArchiCadMappingStaysOpaqueAndSizeLimitIs500Megabytes()
    {
        Assert.Equal(FileFamily.Unclassified, FileTypePolicy.Detect("synthetic opaque"u8, "model.pln").Family);
        Assert.DoesNotContain(FileTypePolicy.AcceptedFormats, format => format.Family == FileFamily.ArchiCad);
        Assert.Equal(524288000L, FileTypePolicy.MaxUploadBytes);
    }
}
