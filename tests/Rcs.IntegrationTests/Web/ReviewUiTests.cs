using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Rcs.IntegrationTests.TestSupport;
using Rcs.Web.Hosting;
using Rcs.Web.Review;
using Rcs.Domain.Vocabulary;

namespace Rcs.IntegrationTests.Web;

/// <summary>
/// The Development-only review build, served as pages: the case workspace really renders the nested workflow from the
/// database, and the review actor cannot be switched on anywhere else.
/// </summary>
public sealed class ReviewUiTests : IAsyncLifetime
{
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
    public void TheReviewActorIsRefusedOutsideDevelopment()
    {
        var exception = Assert.Throws<ReviewModeNotAllowedException>(() =>
            WebHostComposition.Build(["--environment=Production", "--Rcs:Review:Enabled=true"]));

        Assert.Equal("Production", exception.EnvironmentName);
    }

    [Fact]
    public async Task TheCaseListShowsDerivedProgressForEveryCase()
    {
        await using var factory = new ReviewFactory(fixture.Database.RuntimeConnectionString);
        using var client = factory.CreateClient();

        var response = await client.GetAsync(new Uri("/cases", UriKind.Relative));
        var html = WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("RCS Review Build — Development / Synthetic Data", html, StringComparison.Ordinal);
        Assert.Contains("2026/0001", html, StringComparison.Ordinal);
        Assert.Contains("Cavab gözlənilir", html, StringComparison.Ordinal);   // a derived headline, not a stored field
    }

    [Fact]
    public async Task TheCaseWorkspaceRendersTheNestedGraph()
    {
        var nested = (await fixture.Queries.ListAsync(fixture.Chief))
            .Single(item => item.Title.Contains("Torpaq sahəsinin", StringComparison.Ordinal));
        var workspace = await fixture.WorkspaceAsync(nested.Id);
        var requirement = workspace.AllRequirements.Single();
        var child = requirement.ChildRequests.Single();

        await using var factory = new ReviewFactory(fixture.Database.RuntimeConnectionString);
        using var client = factory.CreateClient();

        var response = await client.GetAsync(new Uri($"/cases/{nested.Id}", UriKind.Relative));
        var html = WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(nested.CaseNumber, html, StringComparison.Ordinal);
        Assert.Contains("TMK-77/2026", html, StringComparison.Ordinal);                     // the original incoming letter
        Assert.Contains(child.RequestNumber, html, StringComparison.Ordinal);               // the child request, nested
        Assert.Contains($"requirement-{requirement.Id}", html, StringComparison.Ordinal);   // the requirement between them
        Assert.Contains("KXO-155/2026", html, StringComparison.Ordinal);                    // the answer that fulfilled it
    }

    [Fact]
    public async Task OrganizationsAreListedWithTheirAliases()
    {
        await using var factory = new ReviewFactory(fixture.Database.RuntimeConnectionString);
        using var client = factory.CreateClient();

        var response = await client.GetAsync(new Uri("/organizations", UriKind.Relative));
        var html = WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Kommunal Xidmətlər Orqanı", html, StringComparison.Ordinal);
        Assert.Contains("Kommunal Təsərrüfat İdarəsi", html, StringComparison.Ordinal); // a former name alias
    }

    [Fact]
    public async Task HealthEndpointsStayIndependentOfTheReviewScaffold()
    {
        await using var factory = new ReviewFactory(fixture.Database.RuntimeConnectionString);
        using var client = factory.CreateClient();

        var ready = await client.GetAsync(new Uri("/health/ready", UriKind.Relative));

        Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
        Assert.Equal("Healthy", await ready.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task InspectorSourceKeepsUniqueEntitiesAndActualParallelOutcomes()
    {
        var item = (await fixture.Queries.ListAsync(fixture.Chief)).Single(item => item.CaseNumber == "2026/0001");
        var workspace = await fixture.WorkspaceAsync(item.Id);
        await using var factory = new ReviewFactory(fixture.Database.RuntimeConnectionString);
        using var client = factory.CreateClient();
        var html = WebUtility.HtmlDecode(await client.GetStringAsync(new Uri($"/cases/{item.Id}", UriKind.Relative)));

        Assert.Contains("data-workspace", html, StringComparison.Ordinal);
        Assert.Contains("data-inspector-details", html, StringComparison.Ordinal);
        Assert.Contains("data-inspector-history", html, StringComparison.Ordinal);
        Assert.Contains("data-tone=\"positive\"", html, StringComparison.Ordinal);
        Assert.Contains("data-status=\"Cavab gözlənilir\" data-tone=\"waiting\"", html, StringComparison.Ordinal);
        foreach (var request in workspace.AllRequests)
        {
            Assert.Single(Regex.Matches(html, $"id=\"request-{request.Id}\"", RegexOptions.None, TimeSpan.FromSeconds(1)));
        }

        var allIds = Regex.Matches(html, "\\bid=\"([^\"]+)\"", RegexOptions.None, TimeSpan.FromSeconds(1))
            .Select(match => match.Groups[1].Value).ToArray();
        Assert.Equal(allIds.Length, allIds.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public async Task InspectorStartFormRetainsAntiforgeryAndReturnsToTheSameRequirement()
    {
        var item = (await fixture.Queries.ListAsync(fixture.Chief)).Single(item => item.CaseNumber == "2026/0003");
        var requirement = (await fixture.WorkspaceAsync(item.Id)).AllRequirements.Single();
        await using var factory = new ReviewFactory(fixture.Database.RuntimeConnectionString);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var url = new Uri($"/cases/{item.Id}", UriKind.Relative);
        var html = await client.GetStringAsync(url);
        var form = Regex.Matches(html, "<form\\b[^>]*>[\\s\\S]*?</form>", RegexOptions.None, TimeSpan.FromSeconds(1))
            .Select(match => match.Value).Single(form => form.Contains("?handler=Start", StringComparison.Ordinal));
        var inputs = HiddenInputs(form);
        Assert.Equal(requirement.Id.ToString(), inputs["requirementId"]);

        using var missingToken = new FormUrlEncodedContent(inputs.Where(pair => pair.Key != "__RequestVerificationToken"));
        var denied = await client.PostAsync(new Uri($"/cases/{item.Id}?handler=Start", UriKind.Relative), missingToken);
        Assert.Equal(HttpStatusCode.BadRequest, denied.StatusCode);

        using var validInputs = new FormUrlEncodedContent(inputs);
        var posted = await client.PostAsync(new Uri($"/cases/{item.Id}?handler=Start", UriKind.Relative), validInputs);
        Assert.Equal(HttpStatusCode.Redirect, posted.StatusCode);
        Assert.EndsWith($"#requirement-{requirement.Id}", posted.Headers.Location!.OriginalString, StringComparison.Ordinal);
        Assert.Equal(RequirementStatus.InProgress, (await fixture.WorkspaceAsync(item.Id)).FindRequirement(requirement.Id)!.Status);
    }

    [Fact]
    public async Task CaseCreationFormPreservesValidationAndIdempotentSubmission()
    {
        var baseline = await fixture.Queries.ListAsync(fixture.Chief);
        await using var factory = new ReviewFactory(fixture.Database.RuntimeConnectionString);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var url = new Uri("/cases/new", UriKind.Relative);
        var html = await client.GetStringAsync(url);
        var inputs = HiddenInputs(html);
        inputs["Input.RequestingOrganizationId"] = baseline[0].RequestingOrganization.Id.ToString();
        inputs["Input.Title"] = string.Empty;
        inputs["Input.IncomingLetterNumber"] = "UI-REVIEW-001";
        inputs["Input.LetterDate"] = "2026-09-18";
        using var invalidFields = new FormUrlEncodedContent(inputs);
        var invalid = await client.PostAsync(url, invalidFields);
        Assert.Equal(HttpStatusCode.OK, invalid.StatusCode);
        Assert.Contains("field-validation-error", await invalid.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal(baseline.Count, (await fixture.Queries.ListAsync(fixture.Chief)).Count);

        inputs["Input.Title"] = "UI regression — synthetic case";
        using var validFields = new FormUrlEncodedContent(inputs);
        var created = await client.PostAsync(url, validFields);
        Assert.Equal(HttpStatusCode.Redirect, created.StatusCode);
        using var repeatedFields = new FormUrlEncodedContent(inputs);
        var repeated = await client.PostAsync(url, repeatedFields);
        Assert.Equal(created.Headers.Location, repeated.Headers.Location);
        Assert.Equal(baseline.Count + 1, (await fixture.Queries.ListAsync(fixture.Chief)).Count);
        var workspace = WebUtility.HtmlDecode(await client.GetStringAsync(created.Headers.Location));
        Assert.Contains("UI regression — synthetic case", workspace, StringComparison.Ordinal);
        Assert.Contains("data-kind=\"case\"", workspace, StringComparison.Ordinal);
    }

    private static Dictionary<string, string> HiddenInputs(string html)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (Match input in Regex.Matches(html, "<input\\b[^>]*>", RegexOptions.None, TimeSpan.FromSeconds(1)))
        {
            if (!input.Value.Contains("type=\"hidden\"", StringComparison.Ordinal)) continue;
            var name = Regex.Match(input.Value, "\\bname=\"([^\"]+)\"", RegexOptions.None, TimeSpan.FromSeconds(1));
            var value = Regex.Match(input.Value, "\\bvalue=\"([^\"]*)\"", RegexOptions.None, TimeSpan.FromSeconds(1));
            if (name.Success) result[WebUtility.HtmlDecode(name.Groups[1].Value)] = WebUtility.HtmlDecode(value.Groups[1].Value);
        }

        return result;
    }

    [Fact]
    public async Task InspectorCloseFormKeepsTheOriginalRequestAndVersion()
    {
        var item = (await fixture.Queries.ListAsync(fixture.Chief)).Single(item => item.CaseNumber == "2026/0001");
        var workspace = await fixture.WorkspaceAsync(item.Id);
        var request = workspace.AllRequests.First(request => workspace.Progress.Requests[request.Id].State == Rcs.Domain.Progress.RequestProgressState.AnsweredCloseable);
        await using var factory = new ReviewFactory(fixture.Database.RuntimeConnectionString);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var html = await client.GetStringAsync(new Uri($"/cases/{item.Id}", UriKind.Relative));
        var form = Regex.Matches(html, "<form\\b[^>]*>[\\s\\S]*?</form>", RegexOptions.None, TimeSpan.FromSeconds(1))
            .Select(match => match.Value).Single(form => form.Contains("?handler=CloseRequest", StringComparison.Ordinal)
                && form.Contains($"value=\"{request.Id}\"", StringComparison.Ordinal));
        var inputs = HiddenInputs(form);
        Assert.Equal(request.RowVersion.ToString(System.Globalization.CultureInfo.InvariantCulture), inputs["rowVersion"]);
        using var fields = new FormUrlEncodedContent(inputs);
        var posted = await client.PostAsync(new Uri($"/cases/{item.Id}?handler=CloseRequest", UriKind.Relative), fields);
        Assert.Equal(HttpStatusCode.Redirect, posted.StatusCode);
        Assert.EndsWith($"#request-{request.Id}", posted.Headers.Location!.OriginalString, StringComparison.Ordinal);
        Assert.Equal(RequestStatus.Closed, (await fixture.WorkspaceAsync(item.Id)).FindRequest(request.Id)!.Status);
    }
}
