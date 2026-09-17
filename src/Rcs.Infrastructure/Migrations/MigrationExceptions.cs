using System.Globalization;

namespace Rcs.Infrastructure.Migrations;

public abstract class MigrationException(string message, Exception? innerException = null)
    : Exception(message, innerException)
{
    private protected static string Describe(string summary, IReadOnlyList<string> problems) =>
        problems.Count == 0 ? summary : summary + ":" + Environment.NewLine + string.Join(Environment.NewLine, problems.Select(problem => "  - " + problem));
}

/// <summary>A migration file breaks the file convention. Nothing was executed.</summary>
public sealed class MigrationValidationException(IReadOnlyList<string> problems)
    : MigrationException(Describe("Migration files are invalid", problems))
{
    public IReadOnlyList<string> Problems { get; } = problems;
}

/// <summary>
/// The database history disagrees with this release: an applied migration was edited or renamed, history
/// has gaps, or the database has migrations this release does not know (it is ahead). Nothing was executed.
/// </summary>
public sealed class MigrationHistoryMismatchException(IReadOnlyList<string> problems)
    : MigrationException(Describe("Migration history does not match this release; no migration was run", problems))
{
    public IReadOnlyList<string> Problems { get; } = problems;
}

/// <summary>Another runner held the migration lock for the whole timeout. Nothing was executed.</summary>
public sealed class MigrationLockTimeoutException(TimeSpan timeout)
    : MigrationException(string.Create(CultureInfo.InvariantCulture,
        $"Another migration runner held the migration lock for {timeout.TotalSeconds:0.#} s; giving up. No migration was run."))
{
    public TimeSpan Timeout { get; } = timeout;
}

/// <summary>
/// The bootstrap could not create the history table — typically database/roles/roles.sql was not run, or
/// the database is not owned by the migration role.
/// </summary>
public sealed class MigrationPrerequisiteException(string message, Exception innerException)
    : MigrationException(message, innerException);

/// <summary>
/// A migration failed. A transactional migration was rolled back and is not recorded; later migrations were
/// not attempted.
/// </summary>
public sealed class MigrationExecutionException(Migration migration, Exception innerException)
    : MigrationException(
        $"Migration {migration.FileName} failed and was not recorded as applied; later migrations were not attempted. {innerException.Message}",
        innerException)
{
    public int MigrationId { get; } = migration.Id;

    public string FileName { get; } = migration.FileName;
}
