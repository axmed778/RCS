using Rcs.Application.Previews;

namespace Rcs.Infrastructure.Previews;

/// <summary>How the preview worker is isolated from the application (SECURITY.md §11.5; DECISIONS.md ADR-044).</summary>
public enum PreviewSandboxMode
{
    /// <summary>
    /// bubblewrap: new user, network, PID, IPC and mount namespaces; a filesystem view of /usr, the runtime, the worker
    /// and the job's own directories only (input read-only); a size-limited private /tmp. The default.
    /// </summary>
    Bubblewrap = 1,

    /// <summary>
    /// The worker runs as a plain child process with a scrubbed environment and kernel resource limits, but no
    /// namespace isolation. DEVELOPMENT ONLY — refused at startup in any other environment.
    /// </summary>
    Unisolated,
}

/// <summary><c>Rcs:Preview</c>. Every path is configuration; a relative path resolves against the working directory.</summary>
public sealed class PreviewOptions
{
    public const string SectionName = "Rcs:Preview";

    /// <summary>Master switch. When off, no preview is generated and the UI shows none; downloads are unaffected.</summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Root of the preview store (derived artifacts only). Must not overlap the content-addressed object store: previews
    /// are regenerable cache and may be cleaned; originals never are. Production: <c>/srv/rcs/previews</c>.
    /// </summary>
    public string? StorageRoot { get; set; }

    /// <summary>Per-job staging (input copy, worker output, converter scratch). Production: <c>/srv/rcs/preview-tmp</c>.</summary>
    public string? TempRoot { get; set; }

    /// <summary>Jobs run at the same time in this process. Each is one sandboxed worker.</summary>
    public int WorkerConcurrency { get; set; } = 1;

    public int PollIntervalSeconds { get; set; } = 5;

    /// <summary>How many versions without a preview are queued per reconciliation pass.</summary>
    public int ReconcileBatchSize { get; set; } = 200;

    /// <summary>Automatic attempts for transient failures (timeouts, worker crashes). Bounded: 1–10.</summary>
    public int MaxAttempts { get; set; } = 3;

    /// <summary>First retry delay; doubles per attempt.</summary>
    public int RetryBackoffSeconds { get; set; } = 30;

    /// <summary>Retries a user may request for one failed preview. Operators can always retry from the CLI.</summary>
    public int ManualRetryLimit { get; set; } = 3;

    /// <summary>Wall-clock limit for one whole worker run, enforced by killing the sandbox's process tree.</summary>
    public int JobTimeoutSeconds { get; set; } = 240;

    /// <summary>The worker's own managed heap (DOTNET_GCHeapHardLimit). Converters are limited separately.</summary>
    public int WorkerMemoryMegabytes { get; set; } = 512;

    /// <summary>Size of the sandbox's private /tmp (bubblewrap tmpfs).</summary>
    public int SandboxTempMegabytes { get; set; } = 512;

    /// <summary>
    /// Macro-enabled Office files are download-only by policy (SECURITY.md §10.4).
    /// Configuration validation requires false; an opt-in needs a separate policy decision.
    /// </summary>
    public bool AllowMacroEnabledOffice { get; set; }

    public PreviewSandboxOptions Sandbox { get; set; } = new();

    public PreviewWorkerOptions Worker { get; set; } = new();

    public PreviewToolPaths Tools { get; set; } = new();

    public PreviewLimits Limits { get; set; } = new();

    /// <summary>Identifies the rendering settings on each generation; a change is a reason to regenerate.</summary>
    public string SettingsKey => string.Create(System.Globalization.CultureInfo.InvariantCulture,
        $"v1;px{Limits.MaxPreviewPixels};th{Limits.ThumbnailPixels};p{Limits.MaxPages};dpi{Limits.RenderDpi}");
}

public sealed class PreviewSandboxOptions
{
    public PreviewSandboxMode Mode { get; set; } = PreviewSandboxMode.Bubblewrap;

    public string BubblewrapPath { get; set; } = "/usr/bin/bwrap";

    /// <summary>
    /// Commands placed in front of the sandbox, e.g. <c>["sudo", "-n", "-u", "rcs-preview", "--"]</c> so the worker runs
    /// as its own OS user, or a <c>systemd-run --scope -p MemoryMax=…</c> wrapper for a cgroup memory limit.
    /// </summary>
    public string[] LauncherPrefix { get; set; } = [];

    /// <summary>Extra host paths made visible read-only inside the sandbox (fonts, a converter outside /usr).</summary>
    public string[] ReadOnlyPaths { get; set; } = [];
}

public sealed class PreviewWorkerOptions
{
    /// <summary>
    /// The worker: an apphost or a <c>.dll</c>. Empty = <c>preview-worker/Rcs.PreviewWorker.dll</c> next to the
    /// application (the build copies it there).
    /// </summary>
    public string? Path { get; set; }

    /// <summary>The <c>dotnet</c> host for a <c>.dll</c> worker. Empty = the host running this application.</summary>
    public string? DotnetHost { get; set; }
}
