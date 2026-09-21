using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using Rcs.Infrastructure.Configuration;

namespace Rcs.Infrastructure.Previews;

/// <summary>A job's private directory tree: the read-only input, the worker's output and its scratch space.</summary>
public sealed record PreviewJobDirectory(string Root, string InputDirectory, string OutputDirectory, string WorkDirectory, string JobFile);

/// <summary>
/// The two preview roots, and the only code in RCS that deletes preview files. Both roots hold DERIVED data: the preview
/// store (regenerable artifacts) and the job staging area. They are refused if they overlap the content-addressed object
/// store or its upload area in any direction, so no preview operation can ever reach an original (ADR-043, ADR-044).
/// </summary>
public sealed partial class PreviewPaths
{
    private readonly string? storageRoot;
    private readonly string? tempRoot;

    public PreviewPaths(IOptions<PreviewOptions> options, IOptions<StorageOptions> storage)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(storage);
        storageRoot = Resolve(options.Value.StorageRoot);
        tempRoot = Resolve(options.Value.TempRoot);
        var originals = new[] { Resolve(storage.Value.RootPath), Resolve(storage.Value.TempPath) }.OfType<string>().ToArray();
        foreach (var derived in new[] { storageRoot, tempRoot }.OfType<string>())
        {
            if (originals.Any(original => Overlaps(original, derived)))
            {
                throw new InvalidOperationException("Rcs:Preview roots must not overlap the document object store or its upload area (ADR-044).");
            }
        }

        if (storageRoot is not null && tempRoot is not null && Overlaps(storageRoot, tempRoot))
        {
            throw new InvalidOperationException("Rcs:Preview:StorageRoot and Rcs:Preview:TempRoot must be different directories.");
        }
    }

    public bool IsConfigured => storageRoot is not null && tempRoot is not null;

    public string StorageRoot => storageRoot ?? throw new InvalidOperationException("Rcs:Preview:StorageRoot is not configured.");

    public string TempRoot => tempRoot ?? throw new InvalidOperationException("Rcs:Preview:TempRoot is not configured.");

    /// <summary>The storage key of one artifact: identifiers only, never a filename, title or case number.</summary>
    public static string StorageKey(Guid versionId, Guid previewId, Guid artifactId, string extension) =>
        $"{versionId:D}/{previewId:D}/{artifactId:D}.{extension}";

    public static bool IsValidStorageKey(string? key) => key is not null && StorageKeyPattern().IsMatch(key);

    /// <summary>The absolute path of a stored artifact. Throws for anything that is not a well-formed storage key.</summary>
    public string ArtifactPath(string storageKey)
    {
        if (!IsValidStorageKey(storageKey))
        {
            throw new ArgumentException("Not a preview storage key.", nameof(storageKey));
        }

        return Path.Combine(StorageRoot, storageKey.Replace('/', Path.DirectorySeparatorChar));
    }

    public string GenerationDirectory(Guid versionId, Guid previewId) => Path.Combine(StorageRoot, versionId.ToString("D"), previewId.ToString("D"));

    /// <summary>Creates a fresh job tree with owner-only permissions. The name carries the preview id and a random suffix.</summary>
    public PreviewJobDirectory CreateJobDirectory(Guid previewId)
    {
        var root = Path.Combine(TempRoot, $"job-{previewId:N}-{Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(6))}");
        var job = new PreviewJobDirectory(root, Path.Combine(root, "in"), Path.Combine(root, "out"), Path.Combine(root, "work"), Path.Combine(root, "job.json"));
        Directory.CreateDirectory(TempRoot);
        foreach (var directory in new[] { job.Root, job.InputDirectory, job.OutputDirectory, job.WorkDirectory })
        {
            CreatePrivateDirectory(directory);
        }

        return job;
    }

    /// <summary>A throw-away directory for a capability probe, under the same staging root.</summary>
    public string CreateProbeDirectory()
    {
        var root = Path.Combine(TempRoot, $"job-probe{Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8))}");
        Directory.CreateDirectory(TempRoot);
        CreatePrivateDirectory(root);
        return root;
    }

    /// <summary>
    /// Removes a job tree after its run. Guarded: only a direct child of the staging root whose name is a job name is
    /// ever removed; any other path is refused, whatever the caller passes.
    /// </summary>
    public void DeleteJobDirectory(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        var full = Path.GetFullPath(path);
        if (!string.Equals(Path.GetDirectoryName(full), TempRoot, StringComparison.Ordinal) || !JobNamePattern().IsMatch(Path.GetFileName(full)))
        {
            throw new InvalidOperationException("Refusing to delete a directory outside the preview staging area.");
        }

        if (Directory.Exists(full))
        {
            Directory.Delete(full, recursive: true);
        }
    }

    /// <summary>
    /// Removes the files of a generation whose completion was rejected (its lease was lost). Guarded to
    /// <c>&lt;StorageRoot&gt;/&lt;version-id&gt;/&lt;preview-id&gt;</c>; nothing else can be named.
    /// </summary>
    public void DeleteGenerationDirectory(Guid versionId, Guid previewId)
    {
        var full = Path.GetFullPath(GenerationDirectory(versionId, previewId));
        var expectedParent = Path.Combine(StorageRoot, versionId.ToString("D"));
        if (!string.Equals(Path.GetDirectoryName(full), expectedParent, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Refusing to delete a directory outside the preview store.");
        }

        if (Directory.Exists(full))
        {
            Directory.Delete(full, recursive: true);
        }
    }

    /// <summary>Job trees left behind by a crash, older than <paramref name="age"/>. Returns how many were removed.</summary>
    public int SweepStaleJobDirectories(TimeSpan age)
    {
        if (tempRoot is null || !Directory.Exists(tempRoot))
        {
            return 0;
        }

        var removed = 0;
        foreach (var directory in Directory.EnumerateDirectories(tempRoot))
        {
            if (JobNamePattern().IsMatch(Path.GetFileName(directory)) && Directory.GetLastWriteTimeUtc(directory) < DateTime.UtcNow - age)
            {
                DeleteJobDirectory(directory);
                removed++;
            }
        }

        return removed;
    }

    public static void CreatePrivateDirectory(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(path);
        }
        else
        {
            Directory.CreateDirectory(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    private static string? Resolve(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (full == Path.GetPathRoot(full)) throw new InvalidOperationException("A preview or original storage root cannot be a filesystem root.");
        for (var directory = new DirectoryInfo(full); directory is not null; directory = directory.Parent)
        {
            if (directory.LinkTarget is not null) throw new InvalidOperationException("Preview and original storage roots must not traverse symbolic links.");
        }
        return full;
    }

    private static bool Overlaps(string first, string second)
    {
        var a = Path.TrimEndingDirectorySeparator(first) + Path.DirectorySeparatorChar;
        var b = Path.TrimEndingDirectorySeparator(second) + Path.DirectorySeparatorChar;
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return a.StartsWith(b, comparison) || b.StartsWith(a, comparison);
    }

    [GeneratedRegex(@"^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}/[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}/[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\.(png|jpg|json)\z")]
    private static partial Regex StorageKeyPattern();

    [GeneratedRegex(@"^job-(probe)?[0-9a-f]{12,32}(-[0-9a-f]{12})?\z")]
    private static partial Regex JobNamePattern();
}
