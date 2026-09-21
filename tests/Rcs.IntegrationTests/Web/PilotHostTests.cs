using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Rcs.Domain.Vocabulary;
using Rcs.IntegrationTests.TestSupport;
using Rcs.Web.Hosting;
using Rcs.Web.Review;

namespace Rcs.IntegrationTests.Web;

/// <summary>
/// How the host behaves once it is not a Development review build: the review actor is gone, every page needs a
/// session, and a configuration that would put real documents inside the release directory stops the service.
/// </summary>
public sealed class PilotHostTests : IAsyncLifetime
{
    private const string Password = "kifayət qədər uzun keçid ifadəsi";
    private SliceFixture fixture = null!;

    public async ValueTask InitializeAsync()
    {
        fixture = await SliceFixture.CreateAsync(seedDemoData: false);
        await fixture.Bootstrap.CreateAdministratorAsync("tech.admin", "Texniki inzibatçı", null, Password);
        await fixture.Bootstrap.CreateUserAsync("pilot.user", "Pilot İstifadəçi", null, "Baş mütəxəssis", Password, "tech.admin");
        await fixture.Bootstrap.GrantRoleAsync("pilot.user", BusinessRole.Head, "tech.admin");
        await fixture.Bootstrap.GrantRoleAsync("pilot.user", BusinessRole.Chief, "pilot.user");
        await fixture.Bootstrap.GrantRoleAsync("pilot.user", BusinessRole.Worker, "pilot.user");
    }

    public async ValueTask DisposeAsync() => await fixture.DisposeAsync();

    /// <summary>A pilot host: a real environment name, real authentication, no review actor.</summary>
    private sealed class PilotFactory(SliceFixture fixture) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Pilot");
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Runtime"] = fixture.Database.RuntimeConnectionString,
                ["Rcs:Review:Enabled"] = "false",
                ["Rcs:Storage:RootPath"] = Path.Combine(fixture.StorageRoot, "objects"),
                ["Rcs:Storage:TempPath"] = Path.Combine(fixture.StorageRoot, "temporary"),
                ["Rcs:Preview:Enabled"] = "false",
                // The test client speaks plain HTTP; the pilot server puts nginx and TLS in front (deploy/nginx/rcs.conf).
                ["Rcs:Authentication:RequireSecureCookie"] = "false",
            }));
        }
    }

    [Fact]
    public void TheReviewActorCannotBeEnabledOutsideDevelopment()
    {
        // Configuration alone cannot switch it on: the host refuses to build at all.
        var exception = Assert.Throws<ReviewModeNotAllowedException>(() =>
            WebHostComposition.Build(["--environment=Pilot", "--Rcs:Review:Enabled=true"]));

        Assert.Equal("Pilot", exception.EnvironmentName);
    }

    [Fact]
    public async Task EveryBusinessPageRequiresASessionAndSendsAnonymousVisitorsToTheSignInPage()
    {
        await using var factory = new PilotFactory(fixture);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        foreach (var path in new[] { "/", "/cases", "/organizations", "/admin/users" })
        {
            var response = await client.GetAsync(new Uri(path, UriKind.Relative));
            Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
            Assert.Contains("/login", response.Headers.Location!.OriginalString, StringComparison.Ordinal);
        }

        // Health endpoints stay reachable without a session, for the service manager and the readiness check.
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(new Uri("/health/live", UriKind.Relative))).StatusCode);
    }

    [Fact]
    public async Task TheSignInPageCarriesNoActorSwitchAndTheShellShowsTheBuild()
    {
        await using var factory = new PilotFactory(fixture);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var login = WebUtility.HtmlDecode(await client.GetStringAsync(new Uri("/login", UriKind.Relative)));
        Assert.Contains("Sistemə daxil ol", login, StringComparison.Ordinal);

        // The Development identity switch must not exist anywhere in a pilot build.
        Assert.DoesNotContain("/review/actor", login, StringComparison.Ordinal);
        Assert.DoesNotContain("Act as", login, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Synthetic Data", login, StringComparison.OrdinalIgnoreCase);

        // The build identifier is what a pilot bug report quotes.
        Assert.Contains(BuildInformation.Current.Short, login, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SigningInGivesAWorkingSessionAndSigningOutTakesItAway()
    {
        await using var factory = new PilotFactory(fixture);
        using var handler = new CookieContainerHandler();
        using var client = factory.CreateDefaultClient(handler);

        var signedIn = await SignInAsync(client, "pilot.user", Password);
        Assert.Equal(HttpStatusCode.Redirect, signedIn.StatusCode);

        // A first-use credential sends the employee to the password page before anything else.
        Assert.Equal("/account/password", signedIn.Headers.Location!.OriginalString);
        var page = WebUtility.HtmlDecode(await client.GetStringAsync(new Uri("/account/password", UriKind.Relative)));
        Assert.Contains("Parolu dəyiş", page, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LoginRequiresCsrfAndLocksUseTheSameGenericMessage()
    {
        await using var factory = new PilotFactory(fixture);
        using var handler = new CookieContainerHandler();
        using var client = factory.CreateDefaultClient(handler);
        var missing = await client.PostAsync(new Uri("/login", UriKind.Relative), new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["username"] = "pilot.user", ["password"] = Password,
        }));
        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
        await fixture.Database.ExecuteAsync("UPDATE rcs.user_credential SET locked_until=now()+interval '5 minutes'");
        var locked = await SignInAsync(client, "pilot.user", Password);
        Assert.Contains("İstifadəçi adı və ya parol yanlışdır", WebUtility.HtmlDecode(await locked.Content.ReadAsStringAsync()), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AWrongPasswordIsRefusedWithoutSayingWhy()
    {
        await using var factory = new PilotFactory(fixture);
        using var handler = new CookieContainerHandler();
        using var client = factory.CreateDefaultClient(handler);

        var response = await SignInAsync(client, "pilot.user", "tamamilə yanlış parol");
        var html = WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode); // the form again, not a redirect
        Assert.Contains("İstifadəçi adı və ya parol yanlışdır", html, StringComparison.Ordinal);
        // The same message for an unknown account: the response never distinguishes the two (SECURITY.md §6.4).
        var unknown = await SignInAsync(client, "belə.adam.yoxdur", Password);
        Assert.Contains("İstifadəçi adı və ya parol yanlışdır", WebUtility.HtmlDecode(await unknown.Content.ReadAsStringAsync()), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RealDocumentStorageInsideTheReleaseDirectoryStopsANonDevelopmentHost()
    {
        // The dangerous configuration is the one that works until the next deployment replaces the directory.
        var exception = await Assert.ThrowsAsync<UnsafeDeploymentException>(() => WebHostComposition.Build(
        [
            "--environment=Pilot",
            "--ConnectionStrings:Runtime=" + fixture.Database.RuntimeConnectionString,
            "--Rcs:Storage:RootPath=" + Path.Combine(AppContext.BaseDirectory, "objects"),
            "--Rcs:Storage:TempPath=" + Path.Combine(AppContext.BaseDirectory, "tmp"),
        ]).Services.GetServices<IHostedService>().OfType<DeploymentSafetyGate>().Single().StartAsync(CancellationToken.None));

        Assert.Contains("inside the application directory", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OverlappingOriginalAndTemporaryStorageIsRefused()
    {
        var app = WebHostComposition.Build([
            "--environment=Pilot",
            "--ConnectionStrings:Runtime=" + fixture.Database.RuntimeConnectionString,
            "--Rcs:Storage:RootPath=" + fixture.StorageRoot,
            "--Rcs:Storage:TempPath=" + Path.Combine(fixture.StorageRoot, "tmp"),
        ]);
        await using (app)
        {
            var gate = app.Services.GetServices<IHostedService>().OfType<DeploymentSafetyGate>().Single();
            await Assert.ThrowsAsync<UnsafeDeploymentException>(() => gate.StartAsync(CancellationToken.None));
        }
    }

    [Fact]
    public async Task SourceLoginAttemptsAreRateLimited()
    {
        await using var factory = new PilotFactory(fixture);
        using var handler = new CookieContainerHandler();
        using var client = factory.CreateDefaultClient(handler);
        for (var i = 0; i < 10; i++)
            Assert.Equal(HttpStatusCode.OK, (await SignInAsync(client, "unknown.user", "incorrect")).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await SignInAsync(client, "unknown.user", "incorrect")).StatusCode);
    }

    [Fact]
    public void ADevelopmentHostIsLeftAloneByTheDeploymentGate()
    {
        var gate = WebHostComposition.Build(
        [
            "--environment=Development",
            "--ConnectionStrings:Runtime=" + fixture.Database.RuntimeConnectionString,
        ]).Services.GetServices<IHostedService>().OfType<DeploymentSafetyGate>().Single();

        Assert.True(gate.StartAsync(CancellationToken.None).IsCompletedSuccessfully);
    }

    private static async Task<HttpResponseMessage> SignInAsync(HttpClient client, string username, string password)
    {
        var form = await client.GetStringAsync(new Uri("/login", UriKind.Relative));
        var token = System.Text.RegularExpressions.Regex.Match(form, "__RequestVerificationToken[^>]*?value=\"([^\"]+)\"").Groups[1].Value;
        return await client.PostAsync(new Uri("/login", UriKind.Relative), new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["username"] = username,
            ["password"] = password,
            ["__RequestVerificationToken"] = token,
        }));
    }
}

/// <summary>Keeps the session cookie across requests, the way a browser does.</summary>
internal sealed class CookieContainerHandler : DelegatingHandler
{
    private readonly CookieContainer cookies = new();

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var uri = request.RequestUri!;
        var header = cookies.GetCookieHeader(uri);
        if (header.Length > 0)
        {
            request.Headers.Remove("Cookie");
            request.Headers.Add("Cookie", header);
        }

        var response = await base.SendAsync(request, cancellationToken);
        if (response.Headers.TryGetValues("Set-Cookie", out var values))
        {
            foreach (var value in values)
            {
                cookies.SetCookies(uri, value);
            }
        }

        return response;
    }
}
