using System.Net;
using Rcs.Launcher;

namespace Rcs.UnitTests.Windows;

/// <summary>
/// The desktop launcher of the Windows single-laptop pilot. Everything tested here is what stands between the
/// employee and a terminal: one instance per laptop, a loopback-only address, an environment that is never
/// Development, and waiting for "healthy" rather than "started".
/// </summary>
public sealed class LauncherTests
{
    [Fact]
    public void TheDefaultsAreTheSingleLaptopPilot()
    {
        var options = new LauncherOptions();

        Assert.Null(options.Validate());
        Assert.Equal("http://127.0.0.1:5080", options.Url);
        Assert.Equal("Pilot", options.EnvironmentName);
        Assert.Equal("http://127.0.0.1:5080/health/ready", options.HealthUri.ToString());
        Assert.Equal("http://127.0.0.1:5080/login", options.StartUri.ToString());
    }

    [Theory]
    [InlineData("http://192.168.1.10:5080")]   // a routable address would publish real documents to the network
    [InlineData("http://0.0.0.0:5080")]
    [InlineData("https://rcs.example.lan")]
    [InlineData("not-a-url")]
    public void AnAddressBeyondTheLaptopIsRefused(string url) =>
        Assert.NotNull(new LauncherOptions { Url = url }.Validate());

    [Theory]
    [InlineData("http://127.0.0.1:5080")]
    [InlineData("http://localhost:5080")]
    public void LoopbackAddressesAreAccepted(string url) =>
        Assert.Null(new LauncherOptions { Url = url }.Validate());

    [Fact]
    public void TheLauncherRefusesToStartTheApplicationAsDevelopment()
    {
        // Development is where the review actor replaces authentication: the pilot must never land there.
        var problem = new LauncherOptions { EnvironmentName = "Development" }.Validate();

        Assert.NotNull(problem);
        Assert.Contains("Development", problem, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(4)]
    [InlineData(601)]
    public void AnUnreasonableWaitIsRefused(int seconds) =>
        Assert.NotNull(new LauncherOptions { ReadyTimeoutSeconds = seconds }.Validate());

    [Fact]
    public void MissingSettingsMeanTheDefaults()
    {
        var empty = Directory.CreateTempSubdirectory("rcs-launcher-test-").FullName;

        var options = LauncherOptions.Load(empty);

        Assert.Equal(new LauncherOptions().Url, options.Url);
        Assert.Null(options.Validate());
    }

    [Fact]
    public void SettingsAreReadFromLauncherJson()
    {
        var directory = Directory.CreateTempSubdirectory("rcs-launcher-test-").FullName;
        File.WriteAllText(Path.Combine(directory, "launcher.json"), """
            { "url": "http://127.0.0.1:5099", "environmentName": "Pilot", "readyTimeoutSeconds": 45, "startPath": "/login" }
            """);

        var options = LauncherOptions.Load(directory);

        Assert.Equal("http://127.0.0.1:5099", options.Url);
        Assert.Equal(45, options.ReadyTimeoutSeconds);
        Assert.Null(options.Validate());
    }

    [Fact]
    public void UnreadableSettingsFallBackToDefaultsInsteadOfFailing()
    {
        var directory = Directory.CreateTempSubdirectory("rcs-launcher-test-").FullName;
        File.WriteAllText(Path.Combine(directory, "launcher.json"), "{ this is not json");

        // The employee's icon must still work; a broken settings file is not their problem to debug.
        Assert.Null(LauncherOptions.Load(directory).Validate());
    }

    [Fact]
    public void TheApplicationPathResolvesNextToTheLauncher()
    {
        var options = new LauncherOptions { ApplicationPath = "Rcs.Web.exe" };

        var resolved = options.ResolveApplicationPath(Path.Combine(Path.GetTempPath(), "rcs-install"));

        Assert.True(Path.IsPathFullyQualified(resolved));
        Assert.EndsWith("Rcs.Web.exe", resolved, StringComparison.Ordinal);
    }

    [Fact]
    public void OnlyOneLauncherOwnsTheLaptopAtATime()
    {
        var name = "Global\\RCS.Pilot.Test." + Guid.CreateVersion7().ToString("N");

        Assert.True(SingleInstance.TryAcquire(name, out var first));
        using (first)
        {
            // The second double-click is a second process, so the claim is made from another thread here: a mutex
            // is re-entrant for the thread that already owns it, which would make a same-thread check meaningless.
            Assert.False(AcquiredOnAnotherThread(name));
        }

        // Once the first launcher exits, the icon works again.
        Assert.True(AcquiredOnAnotherThread(name));
    }

    /// <summary>Claims the instance from a separate thread and releases it again, reporting whether it was free.</summary>
    private static bool AcquiredOnAnotherThread(string name)
    {
        var acquired = false;
        var thread = new Thread(() =>
        {
            if (SingleInstance.TryAcquire(name, out var instance))
            {
                acquired = true;
                instance?.Dispose();
            }
        });

        thread.Start();
        thread.Join(TimeSpan.FromSeconds(10));
        return acquired;
    }

    [Fact]
    public async Task WaitingEndsWhenTheApplicationReportsHealthy()
    {
        using var client = new HttpClient(new StubHandler(HttpStatusCode.OK));

        var outcome = await new HealthWaiter(client).WaitAsync(new Uri("http://127.0.0.1:5080/health/ready"), TimeSpan.FromSeconds(5), isRunning: null, TestContext.Current.CancellationToken);

        Assert.Equal(ReadyOutcome.Ready, outcome);
    }

    [Fact]
    public async Task WaitingEndsAtOnceWhenTheApplicationExits()
    {
        using var client = new HttpClient(new StubHandler(HttpStatusCode.ServiceUnavailable));

        // A crashed application must be reported as "did not start", not as a 90-second wait.
        var outcome = await new HealthWaiter(client).WaitAsync(
            new Uri("http://127.0.0.1:5080/health/ready"), TimeSpan.FromSeconds(30), isRunning: () => false, TestContext.Current.CancellationToken);

        Assert.Equal(ReadyOutcome.Stopped, outcome);
    }

    [Fact]
    public async Task WaitingGivesUpAfterTheTimeout()
    {
        using var client = new HttpClient(new StubHandler(HttpStatusCode.ServiceUnavailable));

        var outcome = await new HealthWaiter(client).WaitAsync(
            new Uri("http://127.0.0.1:5080/health/ready"), TimeSpan.FromMilliseconds(600), isRunning: () => true, TestContext.Current.CancellationToken);

        Assert.Equal(ReadyOutcome.TimedOut, outcome);
    }

    private sealed class StubHandler(HttpStatusCode status) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(status));
    }
}
