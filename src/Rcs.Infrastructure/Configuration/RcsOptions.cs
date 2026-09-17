namespace Rcs.Infrastructure.Configuration;

/// <summary>Names under <c>ConnectionStrings</c>. The two credentials are deliberately separate.</summary>
public static class ConnectionStringNames
{
    /// <summary>The running application's role (<c>rcs_app</c>): never a superuser, never a schema owner.</summary>
    public const string Runtime = "Runtime";

    /// <summary>The deployment role (<c>rcs_migrate</c>), used only by the explicit <c>migrate</c> command.</summary>
    public const string Migration = "Migration";
}

/// <summary><c>Rcs:Database</c>.</summary>
public sealed class DatabaseOptions
{
    public const string SectionName = "Rcs:Database";

    /// <summary>
    /// The schema version this release requires. Release-controlled (shipped in appsettings.json), not an
    /// operator override: it must equal the newest migration embedded in the build, and startup fails if not.
    /// </summary>
    public int ExpectedSchemaVersion { get; set; }

    /// <summary>How long <c>migrate</c> waits for another runner to release the migration lock.</summary>
    public int MigrationLockTimeoutSeconds { get; set; } = 60;
}

/// <summary><c>Rcs:Storage</c>. Placeholder for the document object store; nothing reads it in Phase 1.</summary>
public sealed class StorageOptions
{
    public const string SectionName = "Rcs:Storage";

    /// <summary>Root of the content-addressed object store (DOCUMENT_MODEL.md §6, ARCHITECTURE.md §8).</summary>
    public string? RootPath { get; set; }
}
