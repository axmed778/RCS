using System.Text;

namespace Rcs.Infrastructure.Migrations;

/// <summary>
/// The complete, validated, ordered set of migrations of one release. Numbers are unique and contiguous
/// from 0001, so the newest number is the schema version the release requires.
/// </summary>
public sealed class MigrationSet
{
    internal const string EmbeddedMigrationPrefix = "Rcs.Migrations.";
    internal const string EmbeddedBootstrapName = "Rcs.Bootstrap.schema_migration.sql";

    private MigrationSet(IReadOnlyList<Migration> migrations) => Migrations = migrations;

    public IReadOnlyList<Migration> Migrations { get; }

    /// <summary>The schema version this set produces: the newest migration number, or 0 for an empty set.</summary>
    public int LatestVersion => Migrations.Count == 0 ? 0 : Migrations[^1].Id;

    public static MigrationSet Create(IEnumerable<(string FileName, string Content)> files)
    {
        ArgumentNullException.ThrowIfNull(files);

        var problems = new List<string>();
        var parsed = new List<Migration>();
        foreach (var (fileName, content) in files)
        {
            try
            {
                parsed.Add(MigrationParser.Parse(fileName, content));
            }
            catch (MigrationValidationException exception)
            {
                problems.AddRange(exception.Problems);
            }
        }

        foreach (var duplicate in parsed.GroupBy(migration => migration.Id).Where(group => group.Count() > 1))
        {
            problems.Add($"migration number {duplicate.Key:D4} is used by more than one file: {string.Join(", ", duplicate.Select(migration => migration.FileName))}.");
        }

        var ordered = parsed.DistinctBy(migration => migration.Id).OrderBy(migration => migration.Id).ToArray();
        for (var index = 0; index < ordered.Length; index++)
        {
            if (ordered[index].Id != index + 1)
            {
                problems.Add($"migration numbers must be contiguous from 0001: expected {index + 1:D4} but found {ordered[index].FileName}.");
                break;
            }
        }

        if (problems.Count > 0)
        {
            throw new MigrationValidationException(problems);
        }

        return new MigrationSet(ordered);
    }

    /// <summary>The migrations embedded in this build from database/migrations.</summary>
    public static MigrationSet LoadEmbedded()
    {
        var assembly = typeof(MigrationSet).Assembly;
        var files = assembly.GetManifestResourceNames()
            .Where(resource => resource.StartsWith(EmbeddedMigrationPrefix, StringComparison.Ordinal))
            .Select(resource => (resource[EmbeddedMigrationPrefix.Length..], ReadEmbedded(resource)))
            .ToArray();

        if (files.Length == 0)
        {
            throw new InvalidOperationException("No migrations are embedded in Rcs.Infrastructure; the build is broken.");
        }

        return Create(files);
    }

    internal static string ReadEmbedded(string resourceName)
    {
        var assembly = typeof(MigrationSet).Assembly;
        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Embedded SQL resource '{resourceName}' is missing; the build is broken.");
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }
}
