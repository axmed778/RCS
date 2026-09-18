using System.Net;
using System.Net.Http.Headers;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Rcs.Domain.Documents;
using Rcs.Domain.Vocabulary;
using Rcs.IntegrationTests.TestSupport;

namespace Rcs.IntegrationTests.Web;

public sealed class DocumentUiTests : IAsyncLifetime
{
    private SliceFixture fixture = null!;
    public async ValueTask InitializeAsync() => fixture = await SliceFixture.CreateAsync();
    public async ValueTask DisposeAsync() => await fixture.DisposeAsync();

    private sealed class ReviewFactory(SliceFixture fixture, long limit = FileTypePolicy.MaxUploadBytes) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Runtime"] = fixture.Database.RuntimeConnectionString,
                ["Rcs:Storage:RootPath"] = Path.Combine(fixture.StorageRoot, "objects"),
                ["Rcs:Storage:TempPath"] = Path.Combine(fixture.StorageRoot, "temporary"),
                ["Rcs:Storage:MaxUploadBytes"] = limit.ToString(System.Globalization.CultureInfo.InvariantCulture),
            }));
        }
    }

    [Fact]
    public async Task MultipartUploadDownloadDetailsAndInspectorWorkWithOriginalUnicodeFilename()
    {
        var caseId = (await fixture.Queries.ListAsync(fixture.Chief))[0].Id;
        await using var factory = new ReviewFactory(fixture);
        using var client = factory.CreateClient(new() { AllowAutoRedirect = false });
        var form = await client.GetStringAsync(new Uri($"/cases/{caseId}/documents/upload?target=case:{caseId}&role=SUPPORTING", UriKind.Relative));
        Assert.Contains("524288000", form, StringComparison.Ordinal);
        using var body = Multipart(form, caseId, "%PDF-1.7\nsynthetic"u8.ToArray());
        var response = await client.PostAsync(new Uri($"/cases/{caseId}/documents/upload", UriKind.Relative), body);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var placement = Assert.Single((await fixture.DocumentQueries.GetCaseDocumentsAsync(fixture.Chief, caseId)).Value!.Placements);
        Assert.Equal("ərazi-русский.pdf", placement.Shown!.OriginalFileName);
        var details = WebUtility.HtmlDecode(await client.GetStringAsync(new Uri($"/cases/{caseId}/documents/{placement.LinkId}", UriKind.Relative)));
        Assert.Contains("Düzəldilmiş fayl yüklə", details, StringComparison.Ordinal);
        Assert.DoesNotContain("Documents.", details, StringComparison.Ordinal);
        Assert.DoesNotContain(fixture.StorageRoot, details, StringComparison.Ordinal);
        var workspace = await client.GetStringAsync(new Uri($"/cases/{caseId}", UriKind.Relative));
        Assert.Contains("data-inspector-documents", workspace, StringComparison.Ordinal);
        Assert.Contains($"/documents/{placement.LinkId}", workspace, StringComparison.Ordinal);
        var download = await client.GetAsync(new Uri($"/cases/{caseId}/documents/{placement.LinkId}/versions/{placement.Shown.Id}/download", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        Assert.Equal("attachment", download.Content.Headers.ContentDisposition?.DispositionType);
        Assert.Equal("application/octet-stream", download.Content.Headers.ContentType?.MediaType);
        Assert.Contains("no-store", download.Headers.CacheControl!.ToString(), StringComparison.Ordinal);
        Assert.Equal("nosniff", Assert.Single(download.Headers.GetValues("X-Content-Type-Options")));
        Assert.Equal("%PDF-1.7\nsynthetic"u8.ToArray(), await download.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task UploadWithoutAntiforgeryTokenIsRejectedBeforeCreatingFiles()
    {
        var caseId = (await fixture.Queries.ListAsync(fixture.Chief))[0].Id;
        await using var factory = new ReviewFactory(fixture);
        using var client = factory.CreateClient(new() { AllowAutoRedirect = false });
        using var body = Multipart(string.Empty, caseId, "%PDF-1.7"u8.ToArray(), includeToken: false);
        var response = await client.PostAsync(new Uri($"/cases/{caseId}/documents/upload", UriKind.Relative), body);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0L, await fixture.ScalarAsync<long>("SELECT count(*) FROM rcs.document"));
    }

    [Fact]
    public async Task UploadLimitIsEnforcedByServerEvenWhenClientOmitsSizeCheck()
    {
        var caseId = (await fixture.Queries.ListAsync(fixture.Chief))[0].Id;
        await using var factory = new ReviewFactory(fixture, limit: 32);
        using var client = factory.CreateClient(new() { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        var form = await client.GetStringAsync(new Uri($"/cases/{caseId}/documents/upload?target=case:{caseId}", UriKind.Relative));
        using var body = Multipart(form, caseId, new byte[33]);
        var response = await client.PostAsync(new Uri($"/cases/{caseId}/documents/upload", UriKind.Relative), body);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal(0L, await fixture.ScalarAsync<long>("SELECT count(*) FROM rcs.document"));
    }

    [Fact]
    public async Task DuplicateSubmissionConvergesAndRemovedLinkIsNotADownloadCapability()
    {
        var caseId = (await fixture.Queries.ListAsync(fixture.Chief))[0].Id;
        await using var factory = new ReviewFactory(fixture);
        using var client = factory.CreateClient(new() { AllowAutoRedirect = false });
        var form = await client.GetStringAsync(new Uri($"/cases/{caseId}/documents/upload?target=case:{caseId}", UriKind.Relative));
        for (var attempt = 0; attempt < 2; attempt++)
        {
            using var body = Multipart(form, caseId, "%PDF-1.7\nretry"u8.ToArray());
            Assert.Equal(HttpStatusCode.Redirect, (await client.PostAsync(new Uri($"/cases/{caseId}/documents/upload", UriKind.Relative), body)).StatusCode);
        }
        var placement = Assert.Single((await fixture.DocumentQueries.GetCaseDocumentsAsync(fixture.Chief, caseId)).Value!.Placements);
        SliceFixture.Succeeded(await fixture.Documents.WithdrawDocumentAsync(fixture.Chief,
            new(caseId, placement.LinkId, placement.DocumentRowVersion, "UPLOADED_IN_ERROR", "Synthetic incorrect file")));
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(new Uri($"/cases/{caseId}/documents/{placement.LinkId}/versions/{placement.Shown!.Id}/download", UriKind.Relative))).StatusCode);
    }

    [Fact]
    public async Task PinnedLetterPageDoesNotOfferReinstatementWhileALaterVersionIsCurrent()
    {
        // Letter A pins v1 of a map; a later letter in the same case brings v2. The letter's page sees only v1 through its
        // own pin, but must not offer to "reinstate" v1 while v2 is current (ADR-027: only when no version is ACTIVE).
        var workspace = await fixture.WorkspaceAsync((await fixture.Queries.ListAsync(fixture.Chief))[0].Id);
        var caseId = workspace.Header.Id;
        var letterA = workspace.InitiatingLetter!.Id;
        var letterB = workspace.AllRequests.Select(request => request.DispatchLetter?.Id).First(id => id is not null)!.Value;
        using var v1 = new MemoryStream("%PDF-1.7\nmap one"u8.ToArray());
        var first = await fixture.Documents.UploadDocumentAsync(fixture.Chief, new(fixture.NewOperation(), caseId,
            new(DocumentTargetKind.Correspondence, letterA), DocumentLinkRoleCodes.Attachment, "Xəritə", DocumentKindCodes.Other, null, null, null),
            new(v1, "map.pdf", "application/pdf"));
        Assert.True(first.Succeeded, first.Error?.Code);
        using var v2 = new MemoryStream("%PDF-1.7\nmap two"u8.ToArray());
        var second = await fixture.Documents.UploadVersionAsync(fixture.Chief, new(fixture.NewOperation(), caseId, first.Value!.LinkId!.Value, null,
            new(DocumentTargetKind.Correspondence, letterB), DocumentLinkRoleCodes.Annex), new(v2, "map-2.pdf", "application/pdf"));
        Assert.True(second.Succeeded, second.Error?.Code);

        await using var factory = new ReviewFactory(fixture);
        using var client = factory.CreateClient(new() { AllowAutoRedirect = false });
        var letterPage = await client.GetStringAsync(new Uri($"/cases/{caseId}/documents/{first.Value.LinkId}", UriKind.Relative));
        Assert.DoesNotContain("handler=Reinstate", letterPage, StringComparison.Ordinal);
        Assert.Contains($"/versions/{first.Value.VersionId}/download", letterPage, StringComparison.Ordinal);
        Assert.DoesNotContain($"/versions/{second.Value!.VersionId}/download", letterPage, StringComparison.Ordinal);
    }

    private static MultipartFormDataContent Multipart(string form, Guid caseId, byte[] bytes, bool includeToken = true)
    {
        var body = new MultipartFormDataContent();
        foreach (Match input in Regex.Matches(form, "<input[^>]+>", RegexOptions.CultureInvariant))
        {
            var name = Regex.Match(input.Value, "name=\"([^\"]+)\"", RegexOptions.CultureInvariant).Groups[1].Value;
            var value = WebUtility.HtmlDecode(Regex.Match(input.Value, "value=\"([^\"]*)\"", RegexOptions.CultureInvariant).Groups[1].Value);
            if (name is "__RequestVerificationToken" or "operationId") body.Add(new StringContent(value), name);
        }
        if (!includeToken) body.Add(new StringContent(Guid.NewGuid().ToString()), "operationId");
        body.Add(new StringContent($"case:{caseId}"), "target");
        body.Add(new StringContent(DocumentLinkRoleCodes.Supporting), "role");
        body.Add(new StringContent("Sintetik sənəd"), "title");
        body.Add(new StringContent(DocumentKindCodes.Other), "kind");
        body.Add(new StringContent($"/cases/{caseId}"), "returnUrl");
        var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
        body.Add(content, "file", "ərazi-русский.pdf");
        return body;
    }
}
