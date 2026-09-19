using Rcs.Application.Documents;
using Rcs.Application.Persistence;
using Rcs.Application.Previews;
using Rcs.Domain.Documents;
using Rcs.Domain.Vocabulary;
using Rcs.Infrastructure.Persistence;
using Rcs.Infrastructure.Previews;
using Rcs.IntegrationTests.TestSupport;

namespace Rcs.IntegrationTests.Workflow;

public sealed class PreviewTests
{
    [Theory]
    [InlineData("sample.pdf", "READY")]
    [InlineData("sample.png", "READY")]
    [InlineData("sample.jpg", "READY")]
    [InlineData("sample.webp", "READY")]
    [InlineData("sample.doc", "READY")]
    [InlineData("sample.xls", "READY")]
    [InlineData("sample.docx", "READY")]
    [InlineData("sample.xlsx", "READY")]
    [InlineData("sample.kmz", "READY")]
    [InlineData("sample.dxf", "READY")]
    [InlineData("sample.dwg", "UNSUPPORTED")]
    [InlineData("sample.pln", "UNSUPPORTED")]
    [InlineData("failed.pdf", "FAILED")]
    [InlineData("failed.docx", "FAILED")]
    public async Task ActualSandboxWorkerPreservesOriginalAndValidatesArtifacts(string name, string expected)
    {
        await using var fixture = await SliceFixture.CreateAsync();
        var settings = fixture.Service<Microsoft.Extensions.Options.IOptions<PreviewOptions>>().Value;
        settings.MaxAttempts = 1;
        settings.Worker.Path = RepositoryRoot.Combine("src", "Rcs.PreviewWorker", "bin", "Debug", "net10.0", "Rcs.PreviewWorker.dll");
        var bytes = PreviewSamples.Bytes(name);
        var caseId = (await fixture.Queries.ListAsync(fixture.Chief))[0].Id;
        using var stream = new MemoryStream(bytes);
        var uploaded = await fixture.Documents.UploadDocumentAsync(fixture.Chief, new(fixture.NewOperation(), caseId,
            new(DocumentTargetKind.Case, caseId), DocumentLinkRoleCodes.Supporting, name, DocumentKindCodes.Other, null, null, null), new(stream, name, null));
        Assert.True(uploaded.Succeeded, uploaded.Error?.Code);
        var runner = fixture.Service<IPreviewJobRunner>();
        await runner.ReconcileAsync();
        await runner.RunNextAsync();
        Assert.Equal(expected, await fixture.ScalarAsync<string>("SELECT status FROM rcs.document_preview"));
        var download = await fixture.DocumentQueries.OpenDownloadAsync(fixture.Chief, caseId, uploaded.Value!.LinkId!.Value, uploaded.Value.VersionId);
        await using var content = download.Value!;
        using var copy = new MemoryStream();
        await content.Content.CopyToAsync(copy);
        Assert.Equal(bytes, copy.ToArray());
        Assert.Empty((await fixture.Service<IPreviewAdministration>().VerifyAsync()).Findings);
        if (expected == "READY")
        {
            Assert.True(await fixture.ScalarAsync<long>("SELECT count(*) FROM rcs.document_preview_artifact") > 0);
            Assert.Equal(1L, await fixture.ScalarAsync<long>("SELECT count(*) FROM rcs.document_version"));
            if (name == "sample.png")
            {
                var key = await fixture.ScalarAsync<string>("SELECT storage_key FROM rcs.document_preview_artifact LIMIT 1");
                var path = fixture.Service<PreviewPaths>().ArtifactPath(key);
                var artifact = await File.ReadAllBytesAsync(path);
                artifact[^1] ^= 1;
                await File.WriteAllBytesAsync(path, artifact);
                Assert.Contains((await fixture.Service<IPreviewAdministration>().VerifyAsync()).Findings, f => f.Problem.Contains("hash", StringComparison.Ordinal));
                await File.AppendAllTextAsync(path, "changed size");
                Assert.Contains((await fixture.Service<IPreviewAdministration>().VerifyAsync()).Findings, f => f.Problem.Contains("size", StringComparison.Ordinal));
            }
        }
    }

    [Fact]
    public async Task RestrictedCasePinnedCrossCaseAndRemovedLinkUseDownloadSemantics()
    {
        await using var fixture = await SliceFixture.CreateAsync();
        var item = await Upload(fixture); Capabilities(fixture);
        var other = (await fixture.Queries.ListAsync(fixture.Chief)).First(c => c.Id != item.Case).Id;
        var workerId = Rcs.Infrastructure.Development.DemoData.WorkerBUserId;
        await fixture.Database.ExecuteAsync($"UPDATE rcs.case_record SET is_restricted=true, restricted_at=now(), restricted_by_user_id='{fixture.Chief.UserId}' WHERE id='{item.Case}'");
        await fixture.Database.ExecuteAsync($"UPDATE rcs.assignment SET status='ENDED',valid_until=now(),end_reason_id=(SELECT id FROM rcs.assignment_end_reason WHERE code='REASSIGNED') WHERE case_id='{item.Case}' AND assignee_user_id='{workerId}' AND status='ACTIVE'");
        var worker = new Rcs.Application.Common.ActorContext(workerId, "preview-test");
        var query = fixture.Service<IDocumentPreviewQueries>();
        Assert.False((await query.OpenAsync(worker, item.Case, item.Upload.LinkId!.Value, item.Upload.VersionId)).Succeeded);
        Assert.False((await query.RetryAsync(worker, item.Case, item.Upload.LinkId.Value, item.Upload.VersionId)).Succeeded);
        var shared = SliceFixture.Succeeded(await fixture.Documents.PlaceVersionAsync(fixture.Chief, new(fixture.NewOperation(), other,
            item.Upload.LinkId.Value, item.Upload.VersionId, new(DocumentTargetKind.Case, other), DocumentLinkRoleCodes.Supporting, "Explicit synthetic disclosure")));
        Assert.True((await query.OpenAsync(worker, other, shared, item.Upload.VersionId)).Succeeded);
        using var revised = new MemoryStream("%PDF-1.7\nnew version"u8.ToArray());
        var next = await fixture.Documents.UploadVersionAsync(fixture.Chief, new(fixture.NewOperation(), item.Case, item.Upload.LinkId.Value, null, null, null), new(revised, "revised.pdf", null));
        Assert.True(next.Succeeded, next.Error?.Code);
        Assert.False((await query.OpenAsync(worker, other, shared, next.Value!.VersionId)).Succeeded);
        Assert.Empty(await query.GetSummariesAsync(worker, other, [next.Value.VersionId]));
        SliceFixture.Succeeded(await fixture.Documents.RemoveLinkAsync(fixture.Chief, new(other, shared, 1, "Remove synthetic disclosure")));
        Assert.False((await query.OpenAsync(worker, other, shared, item.Upload.VersionId)).Succeeded);
    }

    private static async Task<(Guid Case, UploadOutcome Upload)> Upload(SliceFixture fixture)
    {
        var caseId = (await fixture.Queries.ListAsync(fixture.Chief))[0].Id;
        using var stream = new MemoryStream("%PDF-1.7\nsynthetic preview test\n%%EOF"u8.ToArray());
        var result = await fixture.Documents.UploadDocumentAsync(fixture.Chief, new(fixture.NewOperation(), caseId,
            new(DocumentTargetKind.Case, caseId), DocumentLinkRoleCodes.Supporting, "Preview test", DocumentKindCodes.Other, null, null, null, Float: true),
            new(stream, "test.pdf", "application/pdf"));
        Assert.True(result.Succeeded, result.Error?.Code);
        return (caseId, result.Value!);
    }

    private static void Capabilities(SliceFixture fixture) => fixture.Service<PreviewCapabilityRegistry>().Set(
        [new(PreviewRouting.PdfProcessor, "1", true, null)]);

    [Fact]
    public async Task PendingJobIsDurableUniqueAndLeasedOnlyOnce()
    {
        await using var fixture = await SliceFixture.CreateAsync();
        await Upload(fixture); Capabilities(fixture);
        var runner = fixture.Service<IPreviewJobRunner>();
        Assert.Equal(1, await runner.ReconcileAsync());
        Assert.Equal(0, await runner.ReconcileAsync());
        var factory = fixture.Service<IUnitOfWorkFactory>();
        await using var first = (PostgresUnitOfWork)await factory.BeginAsync();
        var job = await PreviewSql.ClaimAsync(first, Guid.NewGuid(), DateTimeOffset.UtcNow, TimeSpan.FromMinutes(2), default);
        Assert.NotNull(job);
        await using var second = (PostgresUnitOfWork)await factory.BeginAsync();
        Assert.Null(await PreviewSql.ClaimAsync(second, Guid.NewGuid(), DateTimeOffset.UtcNow, TimeSpan.FromMinutes(2), default));
        await first.CommitAsync();
        Assert.Null(await PreviewSql.ClaimAsync(second, Guid.NewGuid(), DateTimeOffset.UtcNow, TimeSpan.FromMinutes(2), default));
    }

    [Fact]
    public async Task ExpiredLeaseIsReclaimedAndOldTokenCannotPublish()
    {
        await using var fixture = await SliceFixture.CreateAsync();
        await Upload(fixture); Capabilities(fixture);
        await fixture.Service<IPreviewJobRunner>().ReconcileAsync();
        var factory = fixture.Service<IUnitOfWorkFactory>();
        var now = DateTimeOffset.UtcNow;
        ClaimedJob old;
        await using (var transaction = (PostgresUnitOfWork)await factory.BeginAsync())
        {
            old = (await PreviewSql.ClaimAsync(transaction, Guid.NewGuid(), now, TimeSpan.FromSeconds(1), default))!;
            await transaction.CommitAsync();
        }
        await using var next = (PostgresUnitOfWork)await factory.BeginAsync();
        var reclaimed = await PreviewSql.ClaimAsync(next, Guid.NewGuid(), now.AddSeconds(2), TimeSpan.FromMinutes(1), default);
        Assert.Equal(old.Preview.Id, reclaimed!.Preview.Id);
        Assert.Equal(2, reclaimed.Preview.AttemptCount);
        Assert.False(await PreviewSql.RecordFailureAsync(next, old.Preview.Id, old.LeaseToken, PreviewStatus.Failed,
            PreviewFailureCodes.WorkerFailed, "Stopped", now, now, default));
        await next.CommitAsync();
    }

    [Fact]
    public async Task ExhaustedLeasesBecomeFailedAndManualRetryIsBounded()
    {
        await using var fixture = await SliceFixture.CreateAsync();
        var item = await Upload(fixture); Capabilities(fixture);
        await fixture.Service<IPreviewJobRunner>().ReconcileAsync();
        var factory = fixture.Service<IUnitOfWorkFactory>();
        var now = DateTimeOffset.UtcNow;
        Guid id = default;
        for (var i = 0; i < 3; i++)
        {
            await using var transaction = (PostgresUnitOfWork)await factory.BeginAsync();
            var claim = await PreviewSql.ClaimAsync(transaction, Guid.NewGuid(), now.AddSeconds(i * 2), TimeSpan.FromSeconds(1), default);
            id = claim!.Preview.Id;
            await transaction.CommitAsync();
        }
        await using (var transaction = (PostgresUnitOfWork)await factory.BeginAsync())
        {
            Assert.Equal(1, await PreviewSql.FailExhaustedLeasesAsync(transaction, now.AddSeconds(10), default));
            Assert.Null(await PreviewSql.ClaimAsync(transaction, Guid.NewGuid(), now.AddSeconds(10), TimeSpan.FromSeconds(1), default));
            Assert.False(await PreviewSql.RetryAsync(transaction, id, fixture.Chief.UserId, 0, now, default));
            Assert.True(await PreviewSql.RetryAsync(transaction, id, fixture.Chief.UserId, 1, now, default));
            await transaction.CommitAsync();
        }
        Assert.Equal("PENDING", await fixture.ScalarAsync<string>($"SELECT status FROM rcs.document_preview WHERE document_version_id='{item.Upload.VersionId}'"));
    }

    [Fact]
    public async Task MetadataAndArtifactsDenyGuessedVersionAndCase()
    {
        await using var fixture = await SliceFixture.CreateAsync();
        var item = await Upload(fixture); Capabilities(fixture);
        await fixture.Service<IPreviewJobRunner>().ReconcileAsync();
        var query = fixture.Service<IDocumentPreviewQueries>();
        Assert.True((await query.OpenAsync(fixture.Chief, item.Case, item.Upload.LinkId!.Value, item.Upload.VersionId)).Succeeded);
        Assert.False((await query.OpenAsync(fixture.Chief, Guid.NewGuid(), item.Upload.LinkId.Value, item.Upload.VersionId)).Succeeded);
        Assert.False((await query.OpenAsync(fixture.Chief, item.Case, item.Upload.LinkId.Value, Guid.NewGuid())).Succeeded);
        Assert.False((await query.OpenArtifactAsync(fixture.Chief, item.Case, item.Upload.LinkId.Value, item.Upload.VersionId, Guid.NewGuid())).Succeeded);
        Assert.Empty(await query.GetSummariesAsync(fixture.Chief, item.Case, [Guid.NewGuid()]));
    }

    [Fact]
    public async Task RegenerationPreservesOriginalHashAndVersionCount()
    {
        await using var fixture = await SliceFixture.CreateAsync();
        var item = await Upload(fixture);
        fixture.Service<PreviewCapabilityRegistry>().Set([new(PreviewRouting.PdfProcessor, "1", false, "missing")]);
        await fixture.Service<IPreviewJobRunner>().ReconcileAsync();
        var before = await fixture.ScalarAsync<string>($"SELECT content_hash FROM rcs.document_version WHERE id='{item.Upload.VersionId}'");
        Assert.Equal("UNSUPPORTED", await fixture.ScalarAsync<string>("SELECT status FROM rcs.document_preview"));
        Capabilities(fixture);
        Assert.True((await fixture.Service<IPreviewAdministration>().RegenerateAsync(item.Upload.VersionId)).Succeeded);
        Assert.Equal(2L, await fixture.ScalarAsync<long>("SELECT count(*) FROM rcs.document_preview"));
        Assert.Equal(1L, await fixture.ScalarAsync<long>("SELECT count(*) FROM rcs.document_version"));
        Assert.Equal(before, await fixture.ScalarAsync<string>($"SELECT content_hash FROM rcs.document_version WHERE id='{item.Upload.VersionId}'"));
    }
}
