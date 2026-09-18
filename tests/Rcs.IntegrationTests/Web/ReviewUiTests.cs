using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Rcs.IntegrationTests.TestSupport;
using Rcs.Web.Hosting;
using Rcs.Web.Review;

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
}
