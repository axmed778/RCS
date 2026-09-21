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
/// <c>Rcs:Authentication</c> — local sign-in (SECURITY.md §6, §8; ADR-033). The defaults are the baselines those
/// sections name; the Argon2id cost is deliberately configurable because §6.2 requires it to be tuned on the real
/// server and recorded with each hash.
/// </summary>
public sealed class LocalAuthenticationOptions
{
    public const string SectionName = "Rcs:Authentication";

    /// <summary>Absolute session lifetime: a session ends at this age however active it has been.</summary>
    public int SessionLifetimeHours { get; set; } = 12;

    /// <summary>Idle timeout (SECURITY.md §8.2): long enough to read a letter, short enough to bound an unattended desk.</summary>
    public int IdleTimeoutMinutes { get; set; } = 60;

    /// <summary>Consecutive failures before the account locks itself for <see cref="LockoutMinutes"/>.</summary>
    public int MaxFailedAttempts { get; set; } = 5;

    /// <summary>The lock releases itself, so an absent administrator cannot cause a permanent lockout (§6.4).</summary>
    public int LockoutMinutes { get; set; } = 15;

    /// <summary>How long an administrative temporary password stays usable before it must be reset again (§6.7).</summary>
    public int TemporaryCredentialHours { get; set; } = 24;

    /// <summary>Sign-in attempts allowed per source address per minute, on top of the per-account lock (§6.4).</summary>
    public int AttemptsPerMinutePerHost { get; set; } = 10;

    /// <summary>Argon2id memory cost in KiB. 65536 = 64 MiB per hash; raise on a server with room and record it.</summary>
    public int Argon2MemoryKibibytes { get; set; } = 65536;

    public int Argon2Iterations { get; set; } = 3;

    public int Argon2Parallelism { get; set; } = 2;

    /// <summary>
    /// Require HTTPS for the session cookie. True by default: a pilot behind the documented nginx/TLS front end keeps
    /// it, and only a deliberately plain-HTTP LAN pilot turns it off — which the readiness checklist records as an exception.
    /// </summary>
    public bool RequireSecureCookie { get; set; } = true;
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
