using System.Text.Json;
using System.Text.Json.Serialization;

namespace Rcs.Launcher;

/// <summary>
/// What the launcher needs to know, read from <c>launcher.json</c> next to the executable. Deliberately tiny and
/// deliberately free of anything secret: the launcher starts a process and opens a browser, so it never reads a
/// connection string, a password or the application's own configuration.
/// </summary>
public sealed record LauncherOptions
{
    /// <summary>The RCS web application to start, relative to the launcher or absolute.</summary>
    public string ApplicationPath { get; init; } = "Rcs.Web.exe";

    /// <summary>
    /// The loopback address the application listens on. A single-laptop pilot is reachable from that laptop only:
    /// 127.0.0.1 is the security boundary, so it is not configurable to a routable address by accident.
    /// </summary>
    public string Url { get; init; } = "http://127.0.0.1:5080";

    /// <summary>The environment name the application runs under. Never "Development": that would enable the review actor.</summary>
    public string EnvironmentName { get; init; } = "Pilot";

    /// <summary>Where the launcher writes its own log. The application writes its own logs separately.</summary>
    public string LogDirectory { get; init; } = @"C:\ProgramData\RCS\Pilot\logs";

    /// <summary>How long to wait for <c>/health/ready</c> before telling the employee something is wrong.</summary>
    public int ReadyTimeoutSeconds { get; init; } = 90;

    /// <summary>The page the browser opens once the application is healthy.</summary>
    public string StartPath { get; init; } = "/login";

    [JsonIgnore]
    public Uri HealthUri => new(new Uri(Url), "/health/ready");

    [JsonIgnore]
    public Uri StartUri => new(new Uri(Url), StartPath);

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>Reads <c>launcher.json</c> beside the executable; missing or unreadable means "use the defaults".</summary>
    public static LauncherOptions Load(string directory)
    {
        ArgumentNullException.ThrowIfNull(directory);
        var path = Path.Combine(directory, "launcher.json");
        if (!File.Exists(path))
        {
            return new LauncherOptions();
        }

        try
        {
            return JsonSerializer.Deserialize<LauncherOptions>(File.ReadAllText(path), Json) ?? new LauncherOptions();
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            return new LauncherOptions();
        }
    }

    /// <summary>
    /// Why these settings cannot be used, or null when they can. Two of these are safety rules rather than typos:
    /// the pilot must not listen beyond the laptop, and it must not run as Development, where the review actor
    /// would replace authentication.
    /// </summary>
    public string? Validate()
    {
        if (!Uri.TryCreate(Url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttp)
        {
            return "Url must be an http:// address on this computer.";
        }

        if (!IsLoopback(uri.Host))
        {
            return "Url must stay on the loopback address (127.0.0.1 or localhost): the single-laptop pilot is not a network service.";
        }

        if (string.Equals(EnvironmentName, "Development", StringComparison.OrdinalIgnoreCase))
        {
            return "EnvironmentName must not be Development: that environment replaces authentication with the demonstration actor.";
        }

        if (ReadyTimeoutSeconds is < 5 or > 600)
        {
            return "ReadyTimeoutSeconds must be between 5 and 600.";
        }

        return null;
    }

    public static bool IsLoopback(string host) =>
        host.Equals("127.0.0.1", StringComparison.Ordinal)
        || host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
        || host.Equals("::1", StringComparison.Ordinal);

    /// <summary>The application's full path, resolved against <paramref name="directory"/> when it is relative.</summary>
    public string ResolveApplicationPath(string directory) =>
        Path.IsPathFullyQualified(ApplicationPath) ? ApplicationPath : Path.GetFullPath(Path.Combine(directory, ApplicationPath));
}
