namespace Rcs.Web.Configuration;

/// <summary><c>Rcs:Hosting</c>. Placeholder: nothing reads it in Phase 1.</summary>
public sealed class HostingOptions
{
    public const string SectionName = "Rcs:Hosting";

    /// <summary>The internal URL staff use, e.g. https://cases.internal.example — never a <c>.local</c> name (PROJECT.md §2).</summary>
    public string? BaseUrl { get; set; }
}

public static class SecretsFileConfigurationExtensions
{
    /// <summary>
    /// Environment variable naming a JSON file with secrets (connection strings). The file lives outside the
    /// source tree with owner-only permissions (ARCHITECTURE.md §16.6, SECURITY.md §13.2). It is added last,
    /// so it takes precedence over other sources.
    /// </summary>
    public const string SecretsFileVariable = "RCS_SECRETS_FILE";

    public static IConfigurationBuilder AddRcsSecretsFile(this IConfigurationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        var path = Environment.GetEnvironmentVariable(SecretsFileVariable);
        return string.IsNullOrWhiteSpace(path)
            ? builder
            : builder.AddJsonFile(Path.GetFullPath(path), optional: false, reloadOnChange: false);
    }
}
