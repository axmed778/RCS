using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Rcs.Infrastructure.Migrations;
using Rcs.UnitTests.TestSupport;

namespace Rcs.UnitTests.Repository;

/// <summary>Guards for decisions that code review alone tends to miss.</summary>
public sealed partial class RepositoryConventionTests
{
    private static IEnumerable<string> MigrationFiles() =>
        Directory.GetFiles(RepositoryRoot.Combine("database", "migrations"), "*.sql").Order(StringComparer.Ordinal);

    private static IEnumerable<string> SqlLines(string path) =>
        File.ReadAllLines(path).Where(line => !line.TrimStart().StartsWith("--", StringComparison.Ordinal));

    [Fact]
    public void EmbeddedMigrationsAreExactlyTheFilesInDatabaseMigrations()
    {
        var onDisk = MigrationSet.Create(MigrationFiles().Select(path => (Path.GetFileName(path), File.ReadAllText(path))));
        var embedded = MigrationSet.LoadEmbedded();

        Assert.Equal(
            onDisk.Migrations.Select(migration => (migration.FileName, migration.Checksum)),
            embedded.Migrations.Select(migration => (migration.FileName, migration.Checksum)));
    }

    [Fact]
    public void ExpectedSchemaVersionInReleaseConfigurationIsTheNewestMigration()
    {
        using var settings = JsonDocument.Parse(File.ReadAllText(RepositoryRoot.Combine("src", "Rcs.Web", "appsettings.json")));
        var expected = settings.RootElement.GetProperty("Rcs").GetProperty("Database").GetProperty("ExpectedSchemaVersion").GetInt32();

        Assert.Equal(MigrationSet.LoadEmbedded().LatestVersion, expected);
    }

    /// <summary>PS-1 (DECISIONS.md): no migration may assume a collation or locale until PS-1 is decided by ADR.</summary>
    [Fact]
    public void MigrationsDoNotAssumeACollationBeforePs1IsDecided()
    {
        var offending = MigrationFiles()
            .SelectMany(path => SqlLines(path).Select(line => (File: Path.GetFileName(path), Line: line)))
            .Where(entry => CollationPattern().IsMatch(entry.Line))
            .ToArray();

        Assert.Empty(offending);
    }

    /// <summary>Only btree_gist is approved (ADR-017); pg_trgm and anything else need an ADR (ADR-024).</summary>
    [Fact]
    public void MigrationsCreateOnlyApprovedExtensions()
    {
        var extensions = MigrationFiles()
            .SelectMany(SqlLines)
            .Select(line => CreateExtensionPattern().Match(line))
            .Where(match => match.Success)
            .Select(match => match.Groups["name"].Value.ToLowerInvariant())
            .Distinct()
            .ToArray();

        Assert.All(extensions, extension => Assert.Equal("btree_gist", extension));
    }

    /// <summary>Identifiers are UUIDv7 from IIdGenerator (ADR-031); random UUIDv4 must not creep into production code.</summary>
    [Fact]
    public void ProductionCodeDoesNotGenerateRandomUuids()
    {
        var offending = Directory.GetFiles(RepositoryRoot.Combine("src"), "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(path => File.ReadAllText(path).Contains("Guid.NewGuid(", StringComparison.Ordinal))
            .ToArray();

        Assert.Empty(offending);
    }

    /// <summary>Domain ← Application ← Infrastructure / Web; no cycles (ARCHITECTURE.md §9).</summary>
    [Fact]
    public void ProjectReferencesPointTowardTheDomain()
    {
        Assert.Empty(ProjectReferences("Rcs.Domain"));
        Assert.Equal(["Rcs.Domain"], ProjectReferences("Rcs.Application"));
        Assert.Equal(["Rcs.Application"], ProjectReferences("Rcs.Infrastructure"));
        Assert.Equal(["Rcs.Application", "Rcs.Infrastructure"], ProjectReferences("Rcs.Web"));

        Assert.Empty(PackageReferences("Rcs.Domain"));
        Assert.Empty(PackageReferences("Rcs.Application"));
    }

    /// <summary>Hand-written SQL owns the schema (ADR-004); no ORM migration tooling, broker, cache or search engine.</summary>
    [Fact]
    public void NoForbiddenPackagesArePinned()
    {
        var packages = XDocument.Load(RepositoryRoot.Combine("Directory.Packages.props"))
            .Descendants("PackageVersion")
            .Select(element => (string)element.Attribute("Include")!)
            .ToArray();

        string[] forbidden = ["Microsoft.EntityFrameworkCore", "StackExchange.Redis", "RabbitMQ", "Confluent.Kafka", "Elastic", "Azure.", "AWSSDK", "Google.Cloud"];
        Assert.DoesNotContain(packages, package => forbidden.Any(prefix => package.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)));
    }

    private static string[] ProjectReferences(string project) =>
        LoadProject(project).Descendants("ProjectReference")
            // Includes use Windows separators, which Path does not recognise on Linux.
            .Select(element => Path.GetFileNameWithoutExtension(((string)element.Attribute("Include")!).Replace('\\', '/')))
            .Order(StringComparer.Ordinal)
            .ToArray();

    private static string[] PackageReferences(string project) =>
        LoadProject(project).Descendants("PackageReference").Select(element => (string)element.Attribute("Include")!).ToArray();

    private static XDocument LoadProject(string project) =>
        XDocument.Load(RepositoryRoot.Combine("src", project, project + ".csproj"));

    [GeneratedRegex(@"\b(COLLATE|LC_COLLATE|LC_CTYPE|ICU_LOCALE|BUILTIN_LOCALE|LOCALE_PROVIDER)\b", RegexOptions.IgnoreCase)]
    private static partial Regex CollationPattern();

    [GeneratedRegex(@"CREATE\s+EXTENSION\s+(?:IF\s+NOT\s+EXISTS\s+)?""?(?<name>[A-Za-z0-9_]+)", RegexOptions.IgnoreCase)]
    private static partial Regex CreateExtensionPattern();
}
