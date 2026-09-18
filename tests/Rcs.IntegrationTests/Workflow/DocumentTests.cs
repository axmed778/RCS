using System.Security.Cryptography;

using Npgsql;
using Rcs.Application.Cases;
using Rcs.Application.Common;
using Rcs.Application.Documents;
using Rcs.Application.Lifecycle;
using Rcs.Application.Workflow;
using Rcs.Domain.Documents;
using Rcs.Domain.Vocabulary;
using Rcs.Infrastructure.Development;
using Rcs.IntegrationTests.TestSupport;

namespace Rcs.IntegrationTests.Workflow;

public sealed class DocumentTests : IAsyncLifetime
{
    private SliceFixture fixture = null!;
    private Guid caseId;
    private static readonly byte[] Pdf = "%PDF-1.7\nSynthetic evidence one\n%%EOF"u8.ToArray();
    private static readonly byte[] Revised = "%PDF-1.7\nSynthetic evidence two\n%%EOF"u8.ToArray();

    public async ValueTask InitializeAsync()
    {
        fixture = await SliceFixture.CreateAsync();
        caseId = (await fixture.Queries.ListAsync(fixture.Chief))[0].Id;
    }

    public async ValueTask DisposeAsync() => await fixture.DisposeAsync();

    private async Task<UploadOutcome> UploadAsync(byte[]? bytes = null, DocumentTarget? target = null, string role = DocumentLinkRoleCodes.Supporting, bool floating = false)
    {
        using var stream = new MemoryStream(bytes ?? Pdf);
        var result = await fixture.Documents.UploadDocumentAsync(fixture.Chief, new UploadDocumentCommand(
            fixture.NewOperation(), caseId, target ?? new(DocumentTargetKind.Case, caseId), role,
            "Sintetik sənəd", DocumentKindCodes.Other, null, null, null, floating), new(stream, "ərazi.pdf", "application/pdf"));
        Assert.True(result.Succeeded, result.Error?.Code);
        return result.Value!;
    }

    private async Task<DocumentPlacementView> PlacementAsync(Guid link) =>
        (await fixture.DocumentQueries.GetCaseDocumentsAsync(fixture.Chief, caseId)).Value!.Find(link)!;

    private async Task<UploadOutcome> ReviseAsync(UploadOutcome first, byte[]? bytes = null)
    {
        using var stream = new MemoryStream(bytes ?? Revised);
        var result = await fixture.Documents.UploadVersionAsync(fixture.Chief,
            new(fixture.NewOperation(), caseId, first.LinkId!.Value, null, null, null), new(stream, "düzəliş.pdf", "application/pdf"));
        Assert.True(result.Succeeded, result.Error?.Code);
        return result.Value!;
    }

    private async Task<byte[]> DownloadAsync(Guid link, Guid version)
    {
        var result = await fixture.DocumentQueries.OpenDownloadAsync(fixture.Chief, caseId, link, version);
        Assert.True(result.Succeeded, result.Error?.Code);
        await using var download = result.Value!;
        using var buffer = new MemoryStream();
        await download.Content.CopyToAsync(buffer);
        return buffer.ToArray();
    }

    [Fact]
    public async Task UploadHashesBytesAndAuditsTheExactAuthorizedDownload()
    {
        var uploaded = await UploadAsync();
        Assert.Equal(Pdf, await DownloadAsync(uploaded.LinkId!.Value, uploaded.VersionId));
        var hash = Convert.ToHexStringLower(SHA256.HashData(Pdf));
        Assert.Equal(hash, await fixture.ScalarAsync<string>($"SELECT content_hash FROM rcs.document_version WHERE id = '{uploaded.VersionId}'"));
        Assert.Equal(1L, await fixture.ScalarAsync<long>($"SELECT count(*) FROM rcs.audit_event WHERE entity_id = '{uploaded.VersionId}' AND action_code = 'DOWNLOAD' AND document_hash = '{hash}'"));
        Assert.Single(Directory.GetFiles(Path.Combine(fixture.StorageRoot, "objects"), "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task IdenticalBytesUnderDifferentDocumentsShareOneObject()
    {
        var first = await UploadAsync();
        var second = await UploadAsync();
        Assert.NotEqual(first.DocumentId, second.DocumentId);
        Assert.NotEqual(first.VersionId, second.VersionId);
        Assert.Single(Directory.GetFiles(Path.Combine(fixture.StorageRoot, "objects"), "*", SearchOption.AllDirectories));
        Assert.Equal(2L, await fixture.ScalarAsync<long>("SELECT count(*) FROM rcs.document_version"));
    }

    [Fact]
    public async Task RetryOfFirstUploadReturnsOriginalWithoutReadingAgain()
    {
        var command = new UploadDocumentCommand(fixture.NewOperation(), caseId, new(DocumentTargetKind.Case, caseId),
            DocumentLinkRoleCodes.Supporting, "Retry", DocumentKindCodes.Other, null, null, null);
        using var bytes = new MemoryStream(Pdf);
        var first = await fixture.Documents.UploadDocumentAsync(fixture.Chief, command, new(bytes, "retry.pdf", null));
        Assert.True(first.Succeeded, first.Error?.Code);
        using var disposed = new MemoryStream();
        disposed.Dispose();
        var replay = await fixture.Documents.UploadDocumentAsync(fixture.Chief, command, new(disposed, "retry.pdf", null));
        Assert.True(replay.Succeeded, replay.Error?.Code);
        Assert.Equal(first.Value!.DocumentId, replay.Value!.DocumentId);
        Assert.Equal(first.Value.VersionId, replay.Value.VersionId);
        Assert.Equal(1L, await fixture.ScalarAsync<long>("SELECT count(*) FROM rcs.document"));
    }

    [Fact]
    public async Task LetterPinKeepsOldBytesWhenTheWorkingCopyGetsANewVersion()
    {
        var first = await UploadAsync(floating: true);
        var letter = (await fixture.WorkspaceAsync(caseId)).InitiatingLetter!.Id;
        var pin = SliceFixture.Succeeded(await fixture.Documents.PlaceVersionAsync(fixture.Chief,
            new(fixture.NewOperation(), caseId, first.LinkId!.Value, first.VersionId,
                new(DocumentTargetKind.Correspondence, letter), DocumentLinkRoleCodes.PrimaryLetter, null)));
        var next = await ReviseAsync(first);
        Assert.Equal(next.VersionId, (await PlacementAsync(first.LinkId.Value)).Shown!.Id);
        Assert.Equal(first.VersionId, (await PlacementAsync(pin)).Shown!.Id);
        Assert.Equal(Pdf, await DownloadAsync(pin, first.VersionId));
        Assert.Equal(Revised, await DownloadAsync(first.LinkId.Value, next.VersionId));
        Assert.False((await fixture.DocumentQueries.OpenDownloadAsync(fixture.Chief, caseId, pin, next.VersionId)).Succeeded);
        var same = await ReviseAsync(first);
        Assert.True(same.ConvergedOnExistingVersion);
        Assert.Equal(next.VersionId, same.VersionId);
        Assert.Equal(2L, await fixture.ScalarAsync<long>("SELECT count(*) FROM rcs.document_version"));
    }

    [Fact]
    public async Task WithdrawalAndReinstatementRetainBothFilesAndOriginalLetterPin()
    {
        var first = await UploadAsync(floating: true);
        var next = await ReviseAsync(first);
        var view = await PlacementAsync(first.LinkId!.Value);
        SliceFixture.Succeeded(await fixture.Documents.WithdrawVersionAsync(fixture.Chief,
            new(caseId, view.LinkId, next.VersionId, view.Shown!.RowVersion, "UPLOADED_IN_ERROR", "Wrong scan", true, "Original scan is correct")));
        view = await PlacementAsync(view.LinkId);
        Assert.Equal(first.VersionId, view.Shown!.Id);
        Assert.Equal(DocumentVersionStatus.Withdrawn, view.Versions.Single(v => v.Id == next.VersionId).Status);
        Assert.Equal(Revised, await DownloadAsync(view.LinkId, next.VersionId));
        Assert.Equal(2, Directory.GetFiles(Path.Combine(fixture.StorageRoot, "objects"), "*", SearchOption.AllDirectories).Length);
    }

    [Fact]
    public async Task RemovedLinkStopsDownloadsButKeepsHistoryAndBytes()
    {
        var first = await UploadAsync();
        var letter = (await fixture.WorkspaceAsync(caseId)).InitiatingLetter!.Id;
        var link = SliceFixture.Succeeded(await fixture.Documents.PlaceVersionAsync(fixture.Chief,
            new(fixture.NewOperation(), caseId, first.LinkId!.Value, first.VersionId,
                new(DocumentTargetKind.Correspondence, letter), DocumentLinkRoleCodes.Attachment, null)));
        SliceFixture.Succeeded(await fixture.Documents.RemoveLinkAsync(fixture.Chief, new(caseId, link, 1, "Wrong letter")));
        Assert.False((await fixture.DocumentQueries.OpenDownloadAsync(fixture.Chief, caseId, link, first.VersionId)).Succeeded);
        Assert.Equal("Wrong letter", (await PlacementAsync(link)).RemovalReason);
        Assert.Equal(Pdf, await DownloadAsync(first.LinkId.Value, first.VersionId));
    }

    [Fact]
    public async Task CorrectionMovesOriginAndPreservesRemovedPlacement()
    {
        var first = await UploadAsync();
        var letter = (await fixture.WorkspaceAsync(caseId)).InitiatingLetter!.Id;
        var moved = SliceFixture.Succeeded(await fixture.Documents.MoveLinkAsync(fixture.Chief,
            new(caseId, first.LinkId!.Value, 1, caseId, new(DocumentTargetKind.Correspondence, letter), DocumentLinkRoleCodes.Attachment, "Wrong context")));
        Assert.False((await PlacementAsync(first.LinkId.Value)).IsActive);
        Assert.True((await PlacementAsync(moved)).IsOrigin);
        Assert.Equal(Pdf, await DownloadAsync(moved, first.VersionId));
    }

    [Fact]
    public async Task EvidenceAndPlacementAreWrittenAndRetractedTogether()
    {
        var workspace = await RequirementWorkspaceAsync();
        caseId = workspace.Header.Id;
        var requirement = workspace.AllRequirements.First(q => !q.Status.IsTerminal());
        var first = await UploadAsync();
        var evidence = SliceFixture.Succeeded(await fixture.Documents.RecordDocumentEvidenceAsync(fixture.Chief,
            new(fixture.NewOperation(), caseId, requirement.Id, first.LinkId!.Value, first.VersionId, "Proof")));
        var placed = (await fixture.DocumentQueries.GetCaseDocumentsAsync(fixture.Chief, caseId)).Value!.For(DocumentTargetKind.Requirement, requirement.Id).Single();
        Assert.Equal(first.VersionId, placed.PinnedVersionId);
        Assert.Contains((await fixture.WorkspaceAsync(caseId)).FindRequirement(requirement.Id)!.Evidence, e => e.Id == evidence && e.DocumentVersionId == first.VersionId);
        SliceFixture.Succeeded(await fixture.Documents.RetractEvidenceAsync(fixture.Chief, new(caseId, evidence, 1, "Wrong evidence")));
        Assert.False((await PlacementAsync(placed.LinkId)).IsActive);
        Assert.Equal("RETRACTED", await fixture.ScalarAsync<string>($"SELECT status FROM rcs.requirement_evidence WHERE id = '{evidence}'"));
        Assert.Equal(Pdf, await DownloadAsync(first.LinkId.Value, first.VersionId));
    }

    [Fact]
    public async Task UploadedDocumentEvidenceSatisfiesTheRealFulfillmentGuard()
    {
        var workspace = await RequirementWorkspaceAsync();
        caseId = workspace.Header.Id;
        var requirement = workspace.AllRequirements.First(q => !q.Status.IsTerminal() && q.ChildRequests.Count == 0);
        await UploadAsync(target: new(DocumentTargetKind.Requirement, requirement.Id), role: DocumentLinkRoleCodes.RequirementEvidence);
        SliceFixture.Succeeded(await fixture.Workflow.FulfillRequirementAsync(fixture.Chief,
            new(caseId, requirement.Id, requirement.RowVersion, null, null)));
        Assert.Equal(RequirementStatus.Fulfilled, (await fixture.WorkspaceAsync(caseId)).FindRequirement(requirement.Id)!.Status);
    }

    [Fact]
    public async Task FinalResultRequiresRealPinnedEvidenceOrAnExplicitHeadOverride()
    {
        var result = SliceFixture.Succeeded(await fixture.Lifecycle.DraftFinalResultAsync(fixture.Chief,
            new(fixture.NewOperation(), caseId, "APPROVAL", "Synthetic decision", null, null)));
        var refused = await fixture.Lifecycle.IssueFinalResultAsync(fixture.Head, new(caseId, result, 1, "Readiness override"));
        Assert.Equal("final_result.document_required", refused.Error?.Code);
        var file = await UploadAsync(floating: true);
        var pinned = SliceFixture.Succeeded(await fixture.Documents.PlaceVersionAsync(fixture.Chief,
            new(fixture.NewOperation(), caseId, file.LinkId!.Value, file.VersionId,
                new(DocumentTargetKind.FinalResult, result), DocumentLinkRoleCodes.FinalResultDocument, null)));
        SliceFixture.Succeeded(await fixture.Lifecycle.IssueFinalResultAsync(fixture.Head, new(caseId, result, 1, "Readiness override")));
        await ReviseAsync(file);
        Assert.Equal(file.VersionId, (await PlacementAsync(pinned)).Shown!.Id);
        Assert.False((await fixture.Documents.RemoveLinkAsync(fixture.Chief, new(caseId, pinned, 1, "Attempt to remove issued evidence"))).Succeeded);
        var version = (await PlacementAsync(file.LinkId.Value)).Versions.Single(v => v.Id == file.VersionId);
        Assert.Equal("document.version_pinned_by_decision", (await fixture.Documents.WithdrawVersionAsync(fixture.Chief,
            new(caseId, file.LinkId.Value, file.VersionId, version.RowVersion, "UPLOADED_IN_ERROR", "Wrong", false, null))).Error?.Code);
    }

    [Fact]
    public async Task HeadCanExplainMissingScanAndAttachItAfterIssue()
    {
        var result = SliceFixture.Succeeded(await fixture.Lifecycle.DraftFinalResultAsync(fixture.Chief,
            new(fixture.NewOperation(), caseId, "APPROVAL", "Signed externally", null, null)));
        SliceFixture.Succeeded(await fixture.Lifecycle.IssueFinalResultAsync(fixture.Head,
            new(caseId, result, 1, "Readiness override", "Scan will follow")));
        Assert.True(await fixture.ScalarAsync<bool>($"SELECT EXISTS (SELECT 1 FROM rcs.audit_event WHERE entity_id = '{result}' AND reason_note LIKE '%D4: Scan will follow%')"));
        await UploadAsync(target: new(DocumentTargetKind.FinalResult, result), role: DocumentLinkRoleCodes.FinalResultDocument);
    }

    [Fact]
    public async Task RestrictedCaseAndHiddenVersionsCannotBeReachedByIdentifier()
    {
        var first = await UploadAsync(floating: true);
        var otherCase = (await fixture.Queries.ListAsync(fixture.Chief)).First(c => c.Id != caseId).Id;
        await fixture.Database.ExecuteAsync($"UPDATE rcs.case_record SET is_restricted = true, restricted_at = now(), restricted_by_user_id = '{DemoData.ReviewActorUserId}' WHERE id = '{caseId}'");
        var workerId = DemoData.WorkerBUserId;
        await fixture.Database.ExecuteAsync($"UPDATE rcs.assignment SET status = 'ENDED', valid_until = now(), end_reason_id = (SELECT id FROM rcs.assignment_end_reason WHERE code = 'REASSIGNED') WHERE case_id = '{caseId}' AND assignee_user_id = '{workerId}' AND status = 'ACTIVE'");
        var worker = new ActorContext(workerId, "document-test");
        Assert.False((await fixture.DocumentQueries.OpenDownloadAsync(worker, caseId, first.LinkId!.Value, first.VersionId)).Succeeded);
        Assert.False((await fixture.DocumentQueries.GetCaseDocumentsAsync(worker, caseId)).Succeeded);
        var shared = SliceFixture.Succeeded(await fixture.Documents.PlaceVersionAsync(fixture.Chief,
            new(fixture.NewOperation(), otherCase, first.LinkId.Value, first.VersionId,
                new(DocumentTargetKind.Case, otherCase), DocumentLinkRoleCodes.Supporting, "Explicit disclosure")));
        var next = await ReviseAsync(first);
        var views = (await fixture.DocumentQueries.GetCaseDocumentsAsync(fixture.Chief, otherCase)).Value!.Find(shared)!;
        Assert.Single(views.Versions);
        Assert.Equal(first.VersionId, views.Shown!.Id);
        Assert.False((await fixture.DocumentQueries.OpenDownloadAsync(fixture.Chief, otherCase, shared, next.VersionId)).Succeeded);
        Assert.False((await fixture.DocumentQueries.OpenDownloadAsync(fixture.Chief, caseId, shared, first.VersionId)).Succeeded);
    }

    [Fact]
    public async Task DocumentWithdrawalKeepsPublishedObjectsAndBlocksItsRemovedLinks()
    {
        var first = await UploadAsync();
        SliceFixture.Succeeded(await fixture.Documents.WithdrawDocumentAsync(fixture.Chief,
            new(caseId, first.LinkId!.Value, 1, "UPLOADED_IN_ERROR", "Wrong document")));
        Assert.False((await fixture.DocumentQueries.OpenDownloadAsync(fixture.Chief, caseId, first.LinkId.Value, first.VersionId)).Succeeded);
        Assert.Single(Directory.GetFiles(Path.Combine(fixture.StorageRoot, "objects"), "*", SearchOption.AllDirectories));
        Assert.Equal("WITHDRAWN", await fixture.ScalarAsync<string>($"SELECT status FROM rcs.document WHERE id = '{first.DocumentId}'"));
    }

    [Fact]
    public async Task IntegrityCheckDetectsMissingAndCorruptBytesWithoutDeletingAnything()
    {
        await UploadAsync();
        Assert.True((await fixture.Integrity.CheckAsync(true, true)).IsClean);
        // Redirect metadata to a different, valid content address using the test administrator; the original bytes stay retained.
        var missingHash = new string('a', 64);
        await fixture.Database.ExecuteAsync($"UPDATE rcs.document_version SET content_hash = '{missingHash}', stored_relative_path = 'sha256/aa/aa/{missingHash}'");
        var report = await fixture.Integrity.CheckAsync(true, true);
        Assert.Contains(report.Findings, finding => finding.Kind == IntegrityFindingKind.MissingObject);
        Assert.Contains(report.Findings, finding => finding.Kind == IntegrityFindingKind.UnreferencedObject);
        Assert.Single(Directory.GetFiles(Path.Combine(fixture.StorageRoot, "objects"), "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task IntegrityCheckDetectsSizeAndHashMismatchWithoutRepairingAnything()
    {
        var uploaded = await UploadAsync();
        var path = Directory.GetFiles(Path.Combine(fixture.StorageRoot, "objects"), "*", SearchOption.AllDirectories).Single();
        if (!OperatingSystem.IsWindows())
        {
            // The store publishes objects read-only; the test plays the corrupting disk, not the application.
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        // Same length, different bytes: only a full re-hash can see it (I4).
        var corrupted = (byte[])Pdf.Clone();
        corrupted[^2] ^= 0x20;
        await File.WriteAllBytesAsync(path, corrupted);
        var quick = await fixture.Integrity.CheckAsync(rehash: false, scanForUnreferenced: false);
        Assert.True(quick.IsClean);
        var full = await fixture.Integrity.CheckAsync(rehash: true, scanForUnreferenced: false);
        Assert.Contains(full.Findings, finding => finding.Kind == IntegrityFindingKind.HashMismatch && finding.VersionId == uploaded.VersionId);

        // Truncated or grown: existence + size is enough (I3).
        await File.WriteAllBytesAsync(path, [.. Pdf, .. "extra"u8.ToArray()]);
        var sized = await fixture.Integrity.CheckAsync(rehash: false, scanForUnreferenced: false);
        Assert.Contains(sized.Findings, finding => finding.Kind == IntegrityFindingKind.SizeMismatch && finding.VersionId == uploaded.VersionId);

        // Nothing was repaired or removed, the version keeps its business status, and a download refuses the bad bytes.
        Assert.True(File.Exists(path));
        Assert.Equal("ACTIVE", await fixture.ScalarAsync<string>($"SELECT status FROM rcs.document_version WHERE id = '{uploaded.VersionId}'"));
        Assert.Equal("document.integrity_failure",
            (await fixture.DocumentQueries.OpenDownloadAsync(fixture.Chief, caseId, uploaded.LinkId!.Value, uploaded.VersionId)).Error?.Code);
        Assert.True(await fixture.ScalarAsync<long>(
            $"SELECT count(*) FROM rcs.audit_event WHERE entity_id = '{uploaded.VersionId}' AND actor_kind = 'JOB' AND after_state->>'integrity_finding' IS NOT NULL") >= 2);
    }

    [Fact]
    public async Task FailedMetadataTransactionLeavesBytesUnreferencedAndNoRows()
    {
        // The database refuses the version row after the bytes were published (§7.2): the operation fails,
        // no metadata exists, and the published object is retained rather than deleted.
        await fixture.Database.ExecuteAsync("REVOKE INSERT ON rcs.document_version FROM rcs_app");
        var command = new UploadDocumentCommand(fixture.NewOperation(), caseId, new(DocumentTargetKind.Case, caseId),
            DocumentLinkRoleCodes.Supporting, "Uğursuz yükləmə", DocumentKindCodes.Other, null, null, null);
        using (var bytes = new MemoryStream(Pdf))
        {
            var failed = await fixture.Documents.UploadDocumentAsync(fixture.Chief, command, new(bytes, "failed.pdf", "application/pdf"));
            Assert.Equal("document.upload_failed", failed.Error?.Code);
        }

        Assert.Equal(0L, await fixture.ScalarAsync<long>("SELECT count(*) FROM rcs.document"));
        Assert.Equal(0L, await fixture.ScalarAsync<long>("SELECT count(*) FROM rcs.document_version"));
        Assert.Equal(0L, await fixture.ScalarAsync<long>($"SELECT count(*) FROM rcs.operation_receipt WHERE operation_id = '{command.OperationId}'"));
        Assert.Single(Directory.GetFiles(Path.Combine(fixture.StorageRoot, "objects"), "*", SearchOption.AllDirectories));
        Assert.Contains((await fixture.Integrity.CheckAsync(false, true)).Findings, finding => finding.Kind == IntegrityFindingKind.UnreferencedObject);

        // Nothing was committed, so the same operation id simply runs again and reuses the retained object.
        await fixture.Database.ExecuteAsync("GRANT INSERT ON rcs.document_version TO rcs_app");
        using var retry = new MemoryStream(Pdf);
        var succeeded = await fixture.Documents.UploadDocumentAsync(fixture.Chief, command, new(retry, "failed.pdf", "application/pdf"));
        Assert.True(succeeded.Succeeded, succeeded.Error?.Code);
        Assert.Single(Directory.GetFiles(Path.Combine(fixture.StorageRoot, "objects"), "*", SearchOption.AllDirectories));
        Assert.True((await fixture.Integrity.CheckAsync(true, true)).IsClean);
    }

    [Fact]
    public async Task RuntimeCannotAlterBytesMetadataOrDeleteDocumentRows()
    {
        var file = await UploadAsync();
        await using var connection = new NpgsqlConnection(fixture.Database.RuntimeConnectionString);
        await connection.OpenAsync();
        foreach (var sql in new[] { $"DELETE FROM rcs.document_version WHERE id = '{file.VersionId}'", $"UPDATE rcs.document_version SET original_filename = 'changed.pdf' WHERE id = '{file.VersionId}'" })
        {
            await using var command = new NpgsqlCommand(sql, connection);
            var error = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync());
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, error.SqlState);
        }
    }

    private async Task<CaseWorkspace> RequirementWorkspaceAsync()
    {
        foreach (var item in await fixture.Queries.ListAsync(fixture.Chief))
        {
            var workspace = await fixture.WorkspaceAsync(item.Id);
            if (workspace.AllRequirements.Any(q => !q.Status.IsTerminal() && q.ChildRequests.Count == 0)) return workspace;
        }
        throw new InvalidOperationException("The synthetic demo should include an open leaf requirement.");
    }
}
