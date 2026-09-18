using System.Buffers;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Rcs.Domain.Documents;
using Rcs.Infrastructure.Configuration;

namespace Rcs.Infrastructure.Documents;

/// <summary>The content address of a SHA-256 hash: <c>sha256/ab/cd/&lt;full hash&gt;</c> (DOCUMENT_MODEL.md §6.2).</summary>
public static class ContentAddress
{
    public const string Algorithm = "SHA256";

    /// <summary>The storage path, derived from the hash and from nothing else — no case, filename or organization.</summary>
    public static string RelativePath(string contentHash)
    {
        if (!IsValidHash(contentHash))
        {
            throw new ArgumentException("A content hash is 64 lower-case hexadecimal characters.", nameof(contentHash));
        }

        return $"sha256/{contentHash[..2]}/{contentHash[2..4]}/{contentHash}";
    }

    public static bool IsValidHash(string? value) =>
        value is { Length: 64 } && value.All(character => character is (>= '0' and <= '9') or (>= 'a' and <= 'f'));
}

/// <summary>Bytes written completely to the temporary area, hashed, flushed to disk, and not yet published.</summary>
/// <param name="Head">The leading bytes, for bounded signature detection (never more than <see cref="FileTypePolicy.SignatureLength"/>).</param>
public sealed record StagedContent(string TemporaryPath, string ContentHash, long ByteSize, byte[] Head);

/// <summary>A published, immutable object. <paramref name="Created"/> is false when identical bytes were already stored.</summary>
public sealed record PublishedContent(string VolumeCode, string RelativePath, string ContentHash, long ByteSize, bool Created);

/// <summary>The upload is larger than the configured maximum (ADR-042: 500 MB). Nothing was published.</summary>
public sealed class UploadTooLargeException(long maxBytes) : Exception($"The upload exceeds the maximum of {maxBytes} bytes.")
{
    public long MaxBytes { get; } = maxBytes;
}

/// <summary>The object store is not configured or not usable. Uploads fail loudly and create no metadata (ARCHITECTURE.md §8.6).</summary>
public sealed class ContentStoreUnavailableException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>What a read of one stored object found.</summary>
public enum ObjectState
{
    Intact = 1,
    Missing,
    SizeMismatch,
    HashMismatch,
    Unreadable,
}

/// <summary>
/// The local filesystem object store (DOCUMENT_MODEL.md §6, §7; ARCHITECTURE.md §8). A directory tree on a local disk —
/// no object-storage service, no cloud SDK, no network (§8.2).
/// </summary>
/// <remarks>
/// <para>
/// The upload protocol of §7.1, first half: bytes are streamed into a randomly named file in the temporary area on the
/// SAME volume, hashed and counted while streaming (the size limit is enforced before a byte beyond it is written),
/// flushed to disk, then published into <c>sha256/ab/cd/&lt;hash&gt;</c> with an atomic, never-overwriting link or rename,
/// and the directory is flushed. Only then may metadata be committed — so a failure leaves at worst an unreferenced
/// object, never a row pointing at missing bytes (§7.2).
/// </para>
/// <para>
/// <b>There is no method that deletes, moves or rewrites a published object</b> (ADR-043: retention is indefinite). The
/// only files this class ever deletes are temporaries that were never published (§7.6, §12.4).
/// </para>
/// </remarks>
public sealed class LocalContentStore
{
    private const int BufferSize = 128 * 1024;
    private readonly StorageOptions options;
    private readonly ILogger<LocalContentStore> logger;

    public LocalContentStore(IOptions<StorageOptions> options, ILogger<LocalContentStore> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        this.options = options.Value;
        this.logger = logger;
        ObjectsRoot = Resolve(this.options.RootPath);
        TemporaryRoot = Resolve(this.options.TempPath);
    }

    public string VolumeCode => options.VolumeCode;

    public long MaxUploadBytes => options.MaxUploadBytes;

    public bool IsConfigured => ObjectsRoot is not null && TemporaryRoot is not null;

    private string? ObjectsRoot { get; }

    private string? TemporaryRoot { get; }

    /// <summary>
    /// Streams <paramref name="source"/> to a new temporary file while hashing it. Throws
    /// <see cref="UploadTooLargeException"/> as soon as the limit is passed; an interrupted or failed read removes the
    /// temporary file. Memory use is one fixed buffer, whatever the file size.
    /// </summary>
    public async Task<StagedContent> StageAsync(Stream source, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        var temporaryRoot = RequireRoots().Temporary;
        var temporaryPath = Path.Combine(temporaryRoot, "upload-" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16)) + ".part");
        var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        var head = new byte[FileTypePolicy.SignatureLength];
        var headLength = 0;
        long total = 0;
        try
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using (var file = new FileStream(temporaryPath, NewFileOptions()))
            {
                int read;
                while ((read = await source.ReadAsync(buffer.AsMemory(0, BufferSize), cancellationToken)) > 0)
                {
                    total += read;
                    if (total > options.MaxUploadBytes)
                    {
                        throw new UploadTooLargeException(options.MaxUploadBytes);
                    }

                    hash.AppendData(buffer, 0, read);
                    if (headLength < head.Length)
                    {
                        var take = Math.Min(read, head.Length - headLength);
                        Buffer.BlockCopy(buffer, 0, head, headLength, take);
                        headLength += take;
                    }

                    await file.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                }

                // Durable before it can be published (§7.1 step 5).
                file.Flush(flushToDisk: true);
            }

            return new StagedContent(temporaryPath, Convert.ToHexStringLower(hash.GetHashAndReset()), total, head[..headLength]);
        }
        catch
        {
            DiscardTemporary(temporaryPath);
            throw;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>
    /// Publishes a staged file at its content address. When identical content is already stored, the existing object
    /// is used and the temporary file is discarded — identical content is identical content (§7.3). An existing object
    /// is never replaced.
    /// </summary>
    public PublishedContent Publish(StagedContent staged)
    {
        ArgumentNullException.ThrowIfNull(staged);
        var roots = RequireRoots();
        EnsureInsideTemporaryArea(staged.TemporaryPath, roots.Temporary);

        var relativePath = ContentAddress.RelativePath(staged.ContentHash);
        var finalPath = FullPath(roots.Objects, relativePath);
        var directory = Path.GetDirectoryName(finalPath)!;
        Directory.CreateDirectory(directory);

        var created = PublishNoReplace(staged.TemporaryPath, finalPath);
        if (created)
        {
            if (!OperatingSystem.IsWindows())
            {
                // Immutable by permission as well as by convention: owner read-only.
                File.SetUnixFileMode(finalPath, UnixFileMode.UserRead);
            }

            NativeFileSystem.FlushDirectory(directory);
        }
        else
        {
            var existing = new FileInfo(finalPath);
            if (existing.Length != staged.ByteSize)
            {
                // The path asserts a hash its bytes do not have. Never "fix" it by overwriting: report it (§11).
                DiscardTemporary(staged.TemporaryPath);
                throw new ContentStoreUnavailableException($"The stored object at {relativePath} does not match its content address.");
            }
        }

        DiscardTemporary(staged.TemporaryPath);
        return new PublishedContent(VolumeCode, relativePath, staged.ContentHash, staged.ByteSize, created);
    }

    /// <summary>Removes a staged file that will not be published — a refused, failed or duplicate upload (§7.6).</summary>
    public void DiscardStaged(StagedContent staged)
    {
        ArgumentNullException.ThrowIfNull(staged);
        EnsureInsideTemporaryArea(staged.TemporaryPath, RequireRoots().Temporary);
        DiscardTemporary(staged.TemporaryPath);
    }

    /// <summary>Opens a stored object for streaming. The caller has already authorized the version.</summary>
    public Stream OpenRead(string volumeCode, string relativePath)
    {
        var path = ObjectPath(volumeCode, relativePath);
        return new FileStream(path, new FileStreamOptions
        {
            Mode = FileMode.Open,
            Access = FileAccess.Read,
            Share = FileShare.Read,
            BufferSize = BufferSize,
            Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
        });
    }

    /// <summary>
    /// Checks one object against its metadata: existence and size, and with <paramref name="expectedHash"/> a full
    /// re-hash. Read-only; nothing is ever repaired here (§11.2).
    /// </summary>
    public async Task<ObjectState> CheckAsync(string volumeCode, string relativePath, long expectedSize, string? expectedHash, CancellationToken cancellationToken)
    {
        string path;
        try
        {
            path = ObjectPath(volumeCode, relativePath);
        }
        catch (ContentStoreUnavailableException)
        {
            return ObjectState.Missing;
        }

        var info = new FileInfo(path);
        if (!info.Exists)
        {
            return ObjectState.Missing;
        }

        if (info.Length != expectedSize)
        {
            return ObjectState.SizeMismatch;
        }

        if (expectedHash is null)
        {
            return ObjectState.Intact;
        }

        try
        {
            await using var stream = OpenRead(volumeCode, relativePath);
            var actual = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellationToken));
            return actual == expectedHash ? ObjectState.Intact : ObjectState.HashMismatch;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(exception, "Stored object {Path} could not be read.", relativePath);
            return ObjectState.Unreadable;
        }
    }

    /// <summary>Every object's relative path in the store — for the unreferenced-object report (I2). Enumerates only.</summary>
    public IEnumerable<string> EnumerateObjects()
    {
        var objects = RequireRoots().Objects;
        var root = Path.Combine(objects, "sha256");
        if (!Directory.Exists(root))
        {
            yield break;
        }

        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            var name = Path.GetFileName(file);
            if (ContentAddress.IsValidHash(name))
            {
                yield return ContentAddress.RelativePath(name);
            }
        }
    }

    /// <summary>
    /// Removes temporaries older than <paramref name="olderThan"/> — interrupted uploads that were never published
    /// (§7.6). The only sweep the store has, and it can only see the temporary area.
    /// </summary>
    public int SweepTemporaries(TimeSpan olderThan)
    {
        if (!IsConfigured || !Directory.Exists(TemporaryRoot))
        {
            return 0;
        }

        var threshold = DateTime.UtcNow - olderThan;
        var removed = 0;
        foreach (var file in Directory.EnumerateFiles(TemporaryRoot!, "upload-*.part", SearchOption.TopDirectoryOnly))
        {
            if (File.GetLastWriteTimeUtc(file) < threshold)
            {
                DiscardTemporary(file);
                removed++;
            }
        }

        return removed;
    }

    private (string Objects, string Temporary) RequireRoots()
    {
        if (ObjectsRoot is null || TemporaryRoot is null)
        {
            throw new ContentStoreUnavailableException("Rcs:Storage:RootPath and Rcs:Storage:TempPath must both be configured before documents can be stored.");
        }

        try
        {
            Directory.CreateDirectory(ObjectsRoot);
            Directory.CreateDirectory(TemporaryRoot);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new ContentStoreUnavailableException("The document store directories could not be created.", exception);
        }

        return (ObjectsRoot, TemporaryRoot);
    }

    private string ObjectPath(string volumeCode, string relativePath)
    {
        if (!string.Equals(volumeCode, VolumeCode, StringComparison.Ordinal))
        {
            throw new ContentStoreUnavailableException($"Storage volume '{volumeCode}' is not configured on this server.");
        }

        var name = relativePath.Split('/')[^1];
        if (!ContentAddress.IsValidHash(name) || ContentAddress.RelativePath(name) != relativePath)
        {
            throw new ContentStoreUnavailableException("Not a content address.");
        }

        return FullPath(RequireRoots().Objects, relativePath);
    }

    private static string FullPath(string root, string relativePath) =>
        Path.Combine([root, .. relativePath.Split('/')]);

    private static string? Resolve(string? configured) =>
        string.IsNullOrWhiteSpace(configured) ? null : Path.GetFullPath(configured);

    private static FileStreamOptions NewFileOptions()
    {
        var fileOptions = new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            Share = FileShare.None,
            BufferSize = 0,
            Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
        };

        if (!OperatingSystem.IsWindows())
        {
            fileOptions.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }

        return fileOptions;
    }

    /// <summary>
    /// Atomic and never overwriting: <c>link(2)</c> on Unix (fails with EEXIST rather than replacing, and with EXDEV
    /// rather than silently copying across volumes, which a plain move would do — §7.3), a same-volume
    /// <c>File.Move</c> without overwrite on Windows. Returns false when the object already exists.
    /// </summary>
    private static bool PublishNoReplace(string temporaryPath, string finalPath)
    {
        if (OperatingSystem.IsWindows())
        {
            if (File.Exists(finalPath))
            {
                return false;
            }

            try
            {
                File.Move(temporaryPath, finalPath, overwrite: false);
                return true;
            }
            catch (IOException) when (File.Exists(finalPath))
            {
                return false;
            }
        }

        return NativeFileSystem.LinkNoReplace(temporaryPath, finalPath);
    }

    private static void EnsureInsideTemporaryArea(string path, string temporaryRoot)
    {
        var full = Path.GetFullPath(path);
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(temporaryRoot)) + Path.DirectorySeparatorChar;
        if (!full.StartsWith(root, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Only files in the temporary upload area may be discarded.");
        }
    }

    /// <summary>The one deletion in this class: an unpublished temporary upload.</summary>
    private void DiscardTemporary(string temporaryPath)
    {
        try
        {
            File.Delete(temporaryPath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Left for the age-based sweep; a temporary is referenced by nothing.
            logger.LogWarning(exception, "Temporary upload {Path} could not be removed yet.", Path.GetFileName(temporaryPath));
        }
    }
}

/// <summary>The two POSIX calls .NET does not expose: a no-replace link and a directory fsync.</summary>
internal static partial class NativeFileSystem
{
    private const int EEXIST = 17;
    private const int EXDEV = 18;

    public static bool LinkNoReplace(string existingPath, string newPath)
    {
        if (Link(Encode(existingPath), Encode(newPath)) == 0)
        {
            return true;
        }

        var error = Marshal.GetLastPInvokeError();
        return error switch
        {
            EEXIST => false,
            EXDEV => throw new ContentStoreUnavailableException(
                "Rcs:Storage:TempPath is not on the same filesystem volume as Rcs:Storage:RootPath; an atomic publish is impossible (ARCHITECTURE.md §8.3)."),
            _ => throw new IOException($"Publishing a stored object failed (errno {error})."),
        };
    }

    /// <summary>Makes the new directory entry durable (§7.1 step 7). A no-op on Windows, where NTFS journals it.</summary>
    public static void FlushDirectory(string directory)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var descriptor = Open(Encode(directory), 0);
        if (descriptor < 0)
        {
            throw new IOException($"The object directory could not be opened for flushing (errno {Marshal.GetLastPInvokeError()}).");
        }

        try
        {
            if (Fsync(descriptor) != 0)
            {
                throw new IOException($"The object directory could not be flushed (errno {Marshal.GetLastPInvokeError()}).");
            }
        }
        finally
        {
            _ = Close(descriptor);
        }
    }

    private static byte[] Encode(string path) => Encoding.UTF8.GetBytes(path + '\0');

    [DllImport("libc", EntryPoint = "link", SetLastError = true)]
    private static extern int Link(byte[] existingPath, byte[] newPath);

    [DllImport("libc", EntryPoint = "open", SetLastError = true)]
    private static extern int Open(byte[] path, int flags);

    [DllImport("libc", EntryPoint = "fsync", SetLastError = true)]
    private static extern int Fsync(int descriptor);

    [DllImport("libc", EntryPoint = "close", SetLastError = true)]
    private static extern int Close(int descriptor);
}
