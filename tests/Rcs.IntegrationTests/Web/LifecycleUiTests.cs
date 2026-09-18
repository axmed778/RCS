using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Rcs.Application.Cases;
using Rcs.Application.Lifecycle;
using Rcs.Application.Workflow;
using Rcs.Domain.Vocabulary;
using Rcs.Domain.Workflow;
using Rcs.Infrastructure.Development;
using Rcs.IntegrationTests.TestSupport;

namespace Rcs.IntegrationTests.Web;

/// <summary>
/// The pages that end a case, served for real: the suggested deadline on the request form, the dashboard's waiting
/// list, the closure screen's warning about unresolved non-blocking work, and the Development-only identity switch
/// that lets the Head's own acts be reviewed as the Head.
/// </summary>
public sealed class LifecycleUiTests : IAsyncLifetime
{
    private static readonly DateOnly Today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, TimeZoneInfo.FindSystemTimeZoneById("Asia/Baku")));

    private SliceFixture fixture = null!;

    public async ValueTask InitializeAsync() => fixture = await SliceFixture.CreateAsync();

    public async ValueTask DisposeAsync() => await fixture.DisposeAsync();

    private sealed class ReviewFactory(string runtimeConnectionString) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Runtime"] = runtimeConnectionString,
            }));
        }
    }

    [Fact]
    public async Task TheRequestFormSuggestsTheDeadlineAndLeavesItEditable()
    {
        var caseId = (await fixture.Queries.ListAsync(fixture.Chief))[0].Id;
        await using var factory = new ReviewFactory(fixture.Database.RuntimeConnectionString);
        using var client = factory.CreateClient();

        var html = WebUtility.HtmlDecode(await client.GetStringAsync(new Uri($"/cases/{caseId}/requests/new", UriKind.Relative)));

        Assert.Contains($"value=\"{Today:yyyy-MM-dd}\"", html, StringComparison.Ordinal);
        Assert.Contains($"value=\"{Today.AddDays(RequestDeadline.DefaultDays):yyyy-MM-dd}\"", html, StringComparison.Ordinal);
        Assert.Contains("data-deadline-to", html, StringComparison.Ordinal);       // the field is an input, not a fixed label
        Assert.Contains("10 təqvim günü", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheDashboardNamesTheRequestsStillWaitingOnAnAuthority()
    {
        await using var factory = new ReviewFactory(fixture.Database.RuntimeConnectionString);
        using var client = factory.CreateClient();

        var html = WebUtility.HtmlDecode(await client.GetStringAsync(new Uri("/", UriKind.Relative)));

        Assert.Contains("Cavab gözlənilən sorğular", html, StringComparison.Ordinal);
        Assert.Contains("Memarlıq Orqanı", html, StringComparison.Ordinal);
    }

    /// <summary>
    /// OB-4 obligation 1: the screen states plainly that unresolved non-blocking requirements remain, names them, and
    /// makes closure a deliberate confirmation. The checkbox is the confirmation, and the command refuses without it.
    /// </summary>
    [Fact]
    public async Task TheClosureScreenWarnsAboutOpenNonBlockingWorkAndRequiresConfirmation()
    {
        var caseId = await CaseReadyToCloseWithOpenNonBlockingWorkAsync();
        await using var factory = new ReviewFactory(fixture.Database.RuntimeConnectionString);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var url = new Uri($"/cases/{caseId}/close", UriKind.Relative);

        var html = WebUtility.HtmlDecode(await client.GetStringAsync(url));
        Assert.Contains("Açıq bloklamayan tələblər", html, StringComparison.Ordinal);
        Assert.Contains("Əlavə sənəd", html, StringComparison.Ordinal);                       // the requirement is named
        Assert.Contains("Input.AcknowledgeUnresolvedNonBlocking", html, StringComparison.Ordinal);

        // Posting without the confirmation is refused, and the case stays open.
        var inputs = HiddenInputs(html);
        inputs["Input.ClosureTypeCode"] = "COMPLETED";
        using var unconfirmed = new FormUrlEncodedContent(inputs);
        var refused = await client.PostAsync(url, unconfirmed);
        Assert.Equal(HttpStatusCode.OK, refused.StatusCode);
        Assert.Equal(CaseLifecycleState.Active, (await fixture.WorkspaceAsync(caseId)).Header.State);

        inputs["Input.AcknowledgeUnresolvedNonBlocking"] = "true";
        using var confirmed = new FormUrlEncodedContent(inputs);
        var closed = await client.PostAsync(url, confirmed);
        Assert.Equal(HttpStatusCode.Redirect, closed.StatusCode);

        // Obligations 2 and 3: the requirement is untouched, and the closed case says what was left open.
        var workspace = await fixture.WorkspaceAsync(caseId);
        Assert.Equal(CaseLifecycleState.Closed, workspace.Header.State);
        Assert.Equal(RequirementStatus.Open, workspace.AllRequirements.Single().Status);
        var page = WebUtility.HtmlDecode(await client.GetStringAsync(new Uri($"/cases/{caseId}", UriKind.Relative)));
        Assert.Contains("açıq bloklamayan tələblər qalır", page, StringComparison.Ordinal);
    }

    /// <summary>
    /// ADR-040: the Chief sees no approval button, and the Head does. The switch is a Development scaffold — it
    /// selects between two synthetic users and nothing else.
    /// </summary>
    [Fact]
    public async Task OnlyTheHeadIsOfferedTheApprovalAndTheSwitchSelectsWhoActs()
    {
        var caseId = await ReadyCaseAsync();
        var resultId = SliceFixture.Succeeded(await fixture.Lifecycle.DraftFinalResultAsync(fixture.Chief, new DraftFinalResultCommand(
            fixture.NewOperation(), caseId, "APPROVAL", "Sintetik qərar mətni", null, null)));
        await fixture.AttachSignedDecisionAsync(caseId, resultId);

        await using var factory = new ReviewFactory(fixture.Database.RuntimeConnectionString);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var workspaceUrl = new Uri($"/cases/{caseId}", UriKind.Relative);

        var asChief = WebUtility.HtmlDecode(await client.GetStringAsync(workspaceUrl));
        Assert.Contains("Nümayiş istifadəçisi (sintetik)", asChief, StringComparison.Ordinal);
        Assert.DoesNotContain("?handler=IssueResult", asChief, StringComparison.Ordinal);
        Assert.Contains("yalnız idarə rəisi", asChief, StringComparison.Ordinal);

        var switched = await client.PostAsync(
            new Uri("/review/actor", UriKind.Relative),
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["username"] = DemoData.ReviewHeadUsername,
                ["returnUrl"] = $"/cases/{caseId}",
                ["__RequestVerificationToken"] = HiddenInputs(asChief)["__RequestVerificationToken"],
            }));
        Assert.Equal(HttpStatusCode.Redirect, switched.StatusCode);

        var asHead = WebUtility.HtmlDecode(await client.GetStringAsync(workspaceUrl));
        Assert.Contains("Nümayiş rəhbəri (sintetik)", asHead, StringComparison.Ordinal);
        Assert.Contains("?handler=IssueResult", asHead, StringComparison.Ordinal);
    }

    /// <summary>A cookie naming anyone else is ignored: the switch can never become a way to act as another user.</summary>
    [Fact]
    public async Task AnUnknownIdentityInTheCookieFallsBackToTheDefaultActor()
    {
        await using var factory = new ReviewFactory(fixture.Database.RuntimeConnectionString);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("Cookie", "rcs_review_actor=someone.else");

        var html = WebUtility.HtmlDecode(await client.GetStringAsync(new Uri("/", UriKind.Relative)));

        Assert.Contains("Nümayiş istifadəçisi (sintetik)", html, StringComparison.Ordinal);
        Assert.DoesNotContain("someone.else", html, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------- fixtures

    /// <summary>A case whose single request is answered and closed, so it is ready for a final result (§7.4).</summary>
    private async Task<Guid> ReadyCaseAsync()
    {
        var caseId = SliceFixture.Succeeded(await fixture.Cases.CreateAsync(fixture.Chief, new CreateCaseCommand(
            fixture.NewOperation(), DemoData.RegionalDevelopmentOfficeId, "Bağlanma ekranı (test)", null,
            "UI-LIFE-IN-1/2026", null, Today.AddDays(-40), Today.AddDays(-39), DemoData.WorkerAUserId, null)));

        var requestId = SliceFixture.Succeeded(await fixture.Workflow.RegisterRequestAsync(fixture.Chief, new RegisterRequestCommand(
            fixture.NewOperation(), caseId, DemoData.ArchitectureAuthorityId, "Rəy sorğusu", "UI-LIFE-OUT-1/2026",
            Today.AddDays(-20), null, null, null)));

        SliceFixture.Succeeded(await fixture.Workflow.RegisterResponseAsync(fixture.Chief, new RegisterResponseCommand(
            fixture.NewOperation(), caseId, requestId, "UI-LIFE-RESP-1/2026", null, Today.AddDays(-3), Today.AddDays(-2),
            "OPINION", "APPROVED", true, "Sintetik cavab")));

        var request = (await fixture.WorkspaceAsync(caseId)).FindRequest(requestId)!;
        SliceFixture.Succeeded(await fixture.Workflow.CloseRequestAsync(
            fixture.Chief, new CloseRequestCommand(caseId, requestId, request.RowVersion, "Cavab alındı.")));
        return caseId;
    }

    /// <summary>The same case, plus one open non-blocking requirement and an issued result, so only OB-4 is in play.</summary>
    private async Task<Guid> CaseReadyToCloseWithOpenNonBlockingWorkAsync()
    {
        var caseId = await ReadyCaseAsync();
        var responseId = (await fixture.WorkspaceAsync(caseId)).AllResponses.Single().Id;

        SliceFixture.Succeeded(await fixture.Workflow.CreateRequirementAsync(fixture.Chief, new CreateRequirementCommand(
            fixture.NewOperation(), caseId, responseId, "Əlavə sənəd", null, false, null, DemoData.UtilityAuthorityId)));

        var resultId = SliceFixture.Succeeded(await fixture.Lifecycle.DraftFinalResultAsync(fixture.Chief, new DraftFinalResultCommand(
            fixture.NewOperation(), caseId, "APPROVAL", "Sintetik qərar mətni", null, null)));
        await fixture.AttachSignedDecisionAsync(caseId, resultId);
        SliceFixture.Succeeded(await fixture.Lifecycle.IssueFinalResultAsync(
            fixture.Head, new IssueFinalResultCommand(caseId, resultId, 1, null)));
        return caseId;
    }

    private static Dictionary<string, string> HiddenInputs(string html)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (Match input in Regex.Matches(html, "<input\\b[^>]*>", RegexOptions.None, TimeSpan.FromSeconds(1)))
        {
            if (!input.Value.Contains("type=\"hidden\"", StringComparison.Ordinal))
            {
                continue;
            }

            var name = Regex.Match(input.Value, "\\bname=\"([^\"]+)\"", RegexOptions.None, TimeSpan.FromSeconds(1));
            var value = Regex.Match(input.Value, "\\bvalue=\"([^\"]*)\"", RegexOptions.None, TimeSpan.FromSeconds(1));
            if (name.Success)
            {
                result[WebUtility.HtmlDecode(name.Groups[1].Value)] = WebUtility.HtmlDecode(value.Groups[1].Value);
            }
        }

        return result;
    }
}
