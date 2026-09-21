using System.Reflection;
using Rcs.Infrastructure.Migrations;

namespace Rcs.Web.Hosting;

/// <summary>
/// Which build is running, so a pilot bug report can name it: "case 2026/0014, workspace page, build 1.0.0+9db12e3".
/// Deliberately thin — a version, a commit and the schema number. No host name, no paths, no framework details, no
/// environment name: a version string is for reproducing a report, not for telling a visitor about the server.
/// </summary>
public sealed record BuildInformation(string Version, string? Commit, int SchemaVersion)
{
    private static readonly Lazy<BuildInformation> Value = new(Read);

    public static BuildInformation Current => Value.Value;

    /// <summary>What the footer shows: <c>1.0.0+9db12e3 · schema 16</c>.</summary>
    public string Short => $"{Version}{(Commit is null ? string.Empty : "+" + Commit)} · schema {SchemaVersion}";

    private static BuildInformation Read()
    {
        var assembly = typeof(BuildInformation).Assembly;
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? assembly.GetName().Version?.ToString()
            ?? "0.0.0";

        // The SDK appends "+<commit sha>" when the build knows one; keep it short and readable.
        var separator = informational.IndexOf('+', StringComparison.Ordinal);
        var version = separator < 0 ? informational : informational[..separator];
        var commit = separator < 0 ? null : informational[(separator + 1)..];
        if (commit is { Length: > 7 })
        {
            commit = commit[..7];
        }

        return new BuildInformation(version, commit, MigrationSet.LoadEmbedded().LatestVersion);
    }
}
