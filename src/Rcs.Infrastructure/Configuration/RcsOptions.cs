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

/// <summary><c>Rcs:Business</c>. How the department's business calendar is interpreted.</summary>
public sealed class BusinessOptions
{
    public const string SectionName = "Rcs:Business";

    /// <summary>
    /// The time zone dates on letters and in forms are read and written in. It decides only how a date becomes an
    /// instant and back; the deadline rules themselves are unresolved (OB-2 / OQ-5).
    /// </summary>
    public string TimeZone { get; set; } = "Asia/Baku";
}

/// <summary>
/// <c>Rcs:Storage</c> — the local content-addressed document store (DOCUMENT_MODEL.md §6; ARCHITECTURE.md §8). Paths are
/// configuration, never code; a relative path resolves against the process working directory (the content root).
/// </summary>
public sealed class StorageOptions
{
    public const string SectionName = "Rcs:Storage";

    /// <summary>Root of the object store: objects live at <c>&lt;RootPath&gt;/sha256/ab/cd/&lt;hash&gt;</c>. Production: <c>/srv/rcs/objects</c>.</summary>
    public string? RootPath { get; set; }

    /// <summary>
    /// The temporary upload area. It MUST be on the same filesystem volume as <see cref="RootPath"/>, because publishing
    /// is an atomic link/rename (ARCHITECTURE.md §8.3); a cross-volume configuration is refused at publish time, never
    /// degraded to a copy. Production: <c>/srv/rcs/tmp-uploads</c>.
    /// </summary>
    public string? TempPath { get; set; }

    /// <summary>The <c>storage_volume_code</c> recorded on every version, naming this root without embedding its path (§6.5).</summary>
    public string VolumeCode { get; set; } = "PRIMARY";

    /// <summary>Maximum bytes per file: 500 MB (ADR-042). Enforced while streaming, and again at the reverse proxy.</summary>
    public long MaxUploadBytes { get; set; } = Rcs.Domain.Documents.FileTypePolicy.MaxUploadBytes;

    /// <summary>Temporary uploads older than this are interrupted uploads and are swept (§7.6). Longer than any plausible upload.</summary>
    public int TemporaryRetentionHours { get; set; } = 24;
}
