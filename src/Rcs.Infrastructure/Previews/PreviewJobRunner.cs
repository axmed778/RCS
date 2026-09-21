using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Rcs.Application.Identifiers;
using Rcs.Application.Persistence;
using Rcs.Application.Previews;
using Rcs.Domain.Documents;
using Rcs.Domain.Vocabulary;
using Rcs.Infrastructure.Documents;
using Rcs.Infrastructure.Persistence;

namespace Rcs.Infrastructure.Previews;

/// <summary>Decides the generation a version gets now: which processor, which version of it, and whether it can run.</summary>
public sealed class PreviewPlanner(PreviewCapabilityRegistry capabilities, IOptions<PreviewOptions> options)
{
    private readonly PreviewOptions settings = options.Value;

    /// <summary>Null when the capability probe has not succeeded yet and the route needs a processor: decide later.</summary>
    internal NewGeneration? Plan(Guid previewId, Guid versionId, string contentHash, string mimeType, Guid? requestedBy, DateTimeOffset now)
    {
        var route = PreviewRouting.Route(mimeType, settings.AllowMacroEnabledOffice);
        var capability = capabilities.Find(route.Processor);
        var attempts = Math.Clamp(settings.MaxAttempts, 1, 10);
        if (!route.IsSupported)
        {
            return new NewGeneration(previewId, versionId, contentHash, PreviewStatus.Unsupported, PreviewType.None, route.Processor,
                capability?.Version ?? "0", "none", route.UnsupportedCode, PreviewMessages.For(route.UnsupportedCode!), attempts, requestedBy, now);
        }

        if (capability is null)
        {
            return null;
        }

        return capability.Available
            ? new NewGeneration(previewId, versionId, contentHash, PreviewStatus.Pending, route.Type, route.Processor,
                capability.Version, settings.SettingsKey, null, null, attempts, requestedBy, now)
            : new NewGeneration(previewId, versionId, contentHash, PreviewStatus.Unsupported, PreviewType.None, route.Processor,
                capability.Version, "none", PreviewFailureCodes.ProcessorUnavailable, PreviewMessages.For(PreviewFailureCodes.ProcessorUnavailable),
                attempts, requestedBy, now);
    }

    public bool Ready => capabilities.Current is not null;

    public Task EnsureCapabilitiesAsync(CancellationToken cancellationToken) => capabilities.EnsureAsync(cancellationToken);
}

/// <summary>Fixed, user-safe failure messages. Converter output never becomes a message.</summary>
public static class PreviewMessages
{
    public static string For(string code) => code switch
    {
        PreviewFailureCodes.FormatNotSupported => "This file format has no preview processor.",
        PreviewFailureCodes.ProcessorUnavailable => "The converter for this format is not available on this server.",
        PreviewFailureCodes.ExcludedByPolicy => "Macro-enabled documents are not previewed by policy.",
        PreviewFailureCodes.Timeout => "The preview did not finish within its time limit.",
        PreviewFailureCodes.Encrypted => "The file is password-protected.",
        PreviewFailureCodes.Malformed => "The file could not be read by the preview processor.",
        PreviewFailureCodes.InputTooLarge => "The file is larger than the preview limit.",
        PreviewFailureCodes.OutputTooLarge => "The preview output exceeded its size limit.",
        PreviewFailureCodes.ImageTooLarge => "The image dimensions exceed the preview limit.",
        PreviewFailureCodes.ArchiveUnsafe => "The archive contains unsafe entries.",
        PreviewFailureCodes.ArchiveTooLarge => "The archive expands beyond the preview limit.",
        PreviewFailureCodes.NoGeometry => "No displayable geometry was found.",
        PreviewFailureCodes.SourceUnavailable => "The stored original could not be read for preview.",
        PreviewFailureCodes.InvalidOutput => "The preview worker returned invalid output.",
        PreviewFailureCodes.WorkerFailed => "The preview worker stopped unexpectedly.",
        _ => "The preview could not be generated.",
    };
}

/// <summary>
/// Runs the preview pipeline: DocumentVersion → job → sandboxed worker → validated artifacts. The original is read,
/// copied into the job's private input directory and its SHA-256 re-checked against <c>content_hash</c> on the way, so
/// the preview is provably derived from exactly the recorded bytes — and the original itself is only ever opened for
/// reading. Everything the worker returns is treated as untrusted output until it passes validation here.
/// </summary>
public sealed class PreviewJobRunner(
    IUnitOfWorkFactory unitOfWorkFactory,
    LocalContentStore store,
    PreviewPaths paths,
    PreviewSandbox sandbox,
    PreviewPlanner planner,
    IIdGenerator ids,
    TimeProvider timeProvider,
    IOptions<PreviewOptions> options,
    ILogger<PreviewJobRunner> logger) : IPreviewJobRunner
{
    private static readonly byte[] PngMagic = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
    private readonly PreviewOptions settings = options.Value;

    public async Task<int> ReconcileAsync(CancellationToken cancellationToken = default)
    {
        await planner.EnsureCapabilitiesAsync(cancellationToken);
        var batch = Math.Clamp(settings.ReconcileBatchSize, 1, 5000);
        var created = 0;
        await using var unitOfWork = (PostgresUnitOfWork)await unitOfWorkFactory.BeginAsync(cancellationToken);
        var now = timeProvider.GetUtcNow();

        foreach (var (versionId, hash, mimeType) in await PreviewSql.VersionsWithoutPreviewAsync(unitOfWork, batch, cancellationToken))
        {
            if (planner.Plan(ids.NewId(), versionId, hash, mimeType, null, now) is { } generation && await PreviewSql.InsertAsync(unitOfWork, generation, cancellationToken))
            {
                created++;
            }
        }

        // A converter installed or a policy relaxed since: an honest UNSUPPORTED becomes a new pending generation.
        if (planner.Ready)
        {
            foreach (var (previewId, versionId, hash, mimeType, _) in await PreviewSql.ReconsiderableAsync(unitOfWork, batch, cancellationToken))
            {
                if (planner.Plan(ids.NewId(), versionId, hash, mimeType, null, now) is { Status: PreviewStatus.Pending } generation
                    && await PreviewSql.SupersedeAsync(unitOfWork, previewId, now, cancellationToken)
                    && await PreviewSql.InsertAsync(unitOfWork, generation, cancellationToken))
                {
                    created++;
                }
            }
        }

        await unitOfWork.CommitAsync(cancellationToken);
        return created;
    }

    public async Task<bool> RunNextAsync(CancellationToken cancellationToken = default)
    {
        var job = await ClaimAsync(cancellationToken);
        if (job is null)
        {
            return false;
        }

        var directory = paths.CreateJobDirectory(job.Preview.Id);
        try
        {
            JobOutcome outcome;
            try
            {
                outcome = await ExecuteAsync(job, directory, cancellationToken);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
            {
                // A local failure while staging or ingesting: no partial generation is published.
                logger.LogError("Preview {PreviewId} failed locally: {Error}", job.Preview.Id, exception.GetType().Name);
                // Never delete a shared generation: a reclaimed lease may already have published there.
                outcome = Failed(PreviewFailureCodes.WorkerFailed);
            }

            await RecordAsync(job, outcome, cancellationToken);
        }
        finally
        {
            try
            {
                paths.DeleteJobDirectory(directory.Root);
            }
            catch (IOException exception)
            {
                logger.LogWarning(exception, "A preview job directory could not be removed; operator cleanup may be required.");
            }
        }

        return true;
    }

    private async Task<ClaimedJob?> ClaimAsync(CancellationToken cancellationToken)
    {
        await using var unitOfWork = (PostgresUnitOfWork)await unitOfWorkFactory.BeginAsync(cancellationToken);
        var now = timeProvider.GetUtcNow();
        var exhausted = await PreviewSql.FailExhaustedLeasesAsync(unitOfWork, now, cancellationToken);
        if (exhausted > 0)
        {
            logger.LogWarning("{Count} preview job(s) abandoned by a stopped worker have no attempts left and are now FAILED.", exhausted);
        }

        // The lease outlives the sandbox's own timeout, so a live job is never reclaimed from under its worker.
        var lease = TimeSpan.FromSeconds(Math.Max(5, settings.JobTimeoutSeconds) + 120);
        var job = await PreviewSql.ClaimAsync(unitOfWork, ids.NewId(), now, lease, cancellationToken);
        await unitOfWork.CommitAsync(cancellationToken);
        return job;
    }

    /// <summary>The result of one run, before it is recorded.</summary>
    private sealed record JobOutcome(PreviewStatus Status, string? FailureCode, string? FailureMessage, PreviewManifest? Manifest, IReadOnlyList<ArtifactRow> Artifacts);

    private static JobOutcome Failed(string code) => new(PreviewStatus.Failed, code, PreviewMessages.For(code), null, []);

    private async Task<JobOutcome> ExecuteAsync(ClaimedJob job, PreviewJobDirectory directory, CancellationToken cancellationToken)
    {
        if (job.ByteSize > settings.Limits.MaxInputBytes)
        {
            return Failed(PreviewFailureCodes.InputTooLarge);
        }
        var extension = FileTypePolicy.CanonicalExtensionOf(job.MimeType) ?? ".bin";
        var input = Path.Combine(directory.InputDirectory, "input" + extension);
        if (!await StageAsync(job, input, cancellationToken))
        {
            return Failed(PreviewFailureCodes.SourceUnavailable);
        }

        var request = new PreviewJobRequest(
            job.Preview.Processor, input, job.MimeType, directory.OutputDirectory, directory.WorkDirectory, settings.Limits, settings.Tools);
        await File.WriteAllTextAsync(directory.JobFile, JsonSerializer.Serialize(request, PreviewProtocol.Json), cancellationToken);

        var run = await sandbox.RunAsync(
            ["run", directory.JobFile],
            new SandboxMounts([directory.JobFile, directory.InputDirectory], [directory.OutputDirectory, directory.WorkDirectory]),
            directory.WorkDirectory,
            cancellationToken);
        if (run.TimedOut)
        {
            logger.LogWarning("Preview {PreviewId} timed out after {Seconds} s and its sandbox was killed.", job.Preview.Id, settings.JobTimeoutSeconds);
            return Failed(PreviewFailureCodes.Timeout);
        }

        var manifestPath = Path.Combine(directory.OutputDirectory, PreviewProtocol.ManifestFileName);
        if (!File.Exists(manifestPath))
        {
            logger.LogWarning("Preview worker for {PreviewId} exited with {Exit} and wrote no manifest: {Error}", job.Preview.Id, run.ExitCode, Bounded(run.StandardError));
            return Failed(PreviewFailureCodes.WorkerFailed);
        }

        var manifest = ReadManifest(manifestPath);
        if (manifest is null || !PreviewManifestValidation.IsValid(manifest, job.Preview.Type, settings.Limits))
        {
            return Failed(PreviewFailureCodes.InvalidOutput);
        }

        if (!string.IsNullOrWhiteSpace(run.StandardError))
        {
            logger.LogDebug("Preview worker diagnostics for {PreviewId}: {Error}", job.Preview.Id, Bounded(run.StandardError));
        }

        var status = VocabularyCodes.AllCodes<PreviewStatus>().Contains(manifest.Status)
            ? VocabularyCodes.FromCode<PreviewStatus>(manifest.Status)
            : PreviewStatus.Pending;
        switch (status)
        {
            case PreviewStatus.Failed or PreviewStatus.Unsupported:
                var code = manifest.FailureCode is { } reported && PreviewFailureCodes.All.Contains(reported) ? reported : PreviewFailureCodes.InvalidOutput;
                return new JobOutcome(status, code, SanitizeMessage(manifest.FailureMessage) ?? PreviewMessages.For(code), manifest, []);
            case PreviewStatus.Ready when run.ExitCode == 0:
                var artifacts = Ingest(job, directory, manifest);
                return artifacts is null ? Failed(PreviewFailureCodes.InvalidOutput) : new JobOutcome(PreviewStatus.Ready, null, null, manifest, artifacts);
            default:
                return Failed(PreviewFailureCodes.InvalidOutput);
        }
    }

    /// <summary>Copies the original into the job's input, re-hashing it: a mismatch means the bytes are not the recorded ones.</summary>
    private async Task<bool> StageAsync(ClaimedJob job, string input, CancellationToken cancellationToken)
    {
        try
        {
            await using (var source = store.OpenRead(job.VolumeCode, job.RelativePath))
            await using (var target = new FileStream(input, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024))
            using (var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            {
                var buffer = new byte[128 * 1024];
                long total = 0;
                int read;
                while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    hash.AppendData(buffer, 0, read);
                    total += read;
                    if (total > settings.Limits.MaxInputBytes || total > job.ByteSize)
                    {
                        return false;
                    }
                    await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                }

                if (total != job.ByteSize || !string.Equals(Convert.ToHexStringLower(hash.GetHashAndReset()), job.ContentHash, StringComparison.Ordinal))
                {
                    logger.LogCritical("Integrity failure while staging preview {PreviewId}: the stored object does not match version {VersionId}.",
                        job.Preview.Id, job.Preview.VersionId);
                    return false;
                }
            }

            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(input, UnixFileMode.UserRead);
            }

            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ContentStoreUnavailableException)
        {
            logger.LogError("The original of preview {PreviewId} could not be read: {Error}", job.Preview.Id, exception.GetType().Name);
            return false;
        }
    }

    private PreviewManifest? ReadManifest(string path)
    {
        var info = new FileInfo(path);
        if (info.LinkTarget is not null || info.Length is <= 0 or > PreviewProtocol.MaxManifestBytes)
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<PreviewManifest>(File.ReadAllBytes(path), PreviewProtocol.Json);
        }
        catch (JsonException)
        {
            logger.LogWarning("A preview worker wrote a malformed manifest.");
            return null;
        }
    }

    /// <summary>
    /// Validates every artifact the worker claims to have produced — name, kind, type, page number, size, magic bytes,
    /// geometry format — and moves the valid set into the preview store under identifier-only names. Any violation
    /// rejects the whole generation; no partial generation is published.
    /// </summary>
    private List<ArtifactRow>? Ingest(ClaimedJob job, PreviewJobDirectory directory, PreviewManifest manifest)
    {
        var limits = settings.Limits;
        var type = manifest.PreviewType is { } reported && VocabularyCodes.AllCodes<PreviewType>().Contains(reported)
            ? VocabularyCodes.FromCode<PreviewType>(reported)
            : (PreviewType?)null;
        if (type != job.Preview.Type || manifest.Artifacts is not { Count: > 0 } claimed || claimed.Count > limits.MaxPages + 3
            || claimed.Select(artifact => artifact.File).Distinct(StringComparer.Ordinal).Count() != claimed.Count)
        {
            return Reject(job, "the manifest does not describe a valid generation");
        }

        var rendered = manifest.PagesRendered ?? 0;
        var staged = new List<(PreviewManifestArtifact Claim, PreviewArtifactKind Kind, string Path, long Size)>();
        long total = 0;
        foreach (var artifact in claimed)
        {
            if (!PreviewArtifactFiles.IsValid(artifact.File) || !VocabularyCodes.AllCodes<PreviewArtifactKind>().Contains(artifact.Kind))
            {
                return Reject(job, "an artifact has an invalid name or kind");
            }

            var kind = VocabularyCodes.FromCode<PreviewArtifactKind>(artifact.Kind);
            var expectedType = Path.GetExtension(artifact.File) switch
            {
                ".png" => "image/png",
                ".jpg" => "image/jpeg",
                _ => "application/json",
            };
            var consistent = artifact.ContentType == expectedType && kind switch
            {
                PreviewArtifactKind.Page => type == PreviewType.Pages && artifact.PageNumber is { } page && page >= 1 && page <= rendered
                                            && artifact.File == PreviewArtifactFiles.Page(page),
                PreviewArtifactKind.Thumbnail => artifact.File == PreviewArtifactFiles.Thumbnail && artifact.PageNumber is null,
                PreviewArtifactKind.Image => type == PreviewType.Image && artifact.File is PreviewArtifactFiles.ImagePng or PreviewArtifactFiles.ImageJpeg,
                PreviewArtifactKind.Geometry => type == PreviewType.Geometry && artifact.File == PreviewArtifactFiles.Geometry,
                _ => false,
            };
            if (!consistent || artifact.Width is < 1 or > 65535 || artifact.Height is < 1 or > 65535)
            {
                return Reject(job, "an artifact is inconsistent with its kind");
            }

            var path = Path.Combine(directory.OutputDirectory, artifact.File);
            var info = new FileInfo(path);
            if (!info.Exists || info.LinkTarget is not null || info.Length <= 0 || info.Length > limits.MaxArtifactBytes || (total += info.Length) > limits.MaxOutputBytes)
            {
                return Reject(job, "an artifact is missing, linked or too large");
            }

            if (!HasExpectedContent(path, expectedType))
            {
                return Reject(job, "an artifact's content does not match its type");
            }

            staged.Add((artifact, kind, path, info.Length));
        }

        var main = type switch
        {
            PreviewType.Pages => staged.Count(item => item.Kind == PreviewArtifactKind.Page) == rendered && rendered > 0,
            PreviewType.Image => staged.Any(item => item.Kind == PreviewArtifactKind.Image),
            PreviewType.Geometry => staged.Any(item => item.Kind == PreviewArtifactKind.Geometry),
            _ => false,
        };
        if (!main)
        {
            return Reject(job, "the generation lacks its main artifact");
        }

        var generation = paths.GenerationDirectory(job.Preview.VersionId, job.Preview.Id);
        Directory.CreateDirectory(generation);
        var rows = new List<ArtifactRow>(staged.Count);
        foreach (var (claim, kind, source, size) in staged)
        {
            var artifactId = ids.NewId();
            var key = PreviewPaths.StorageKey(job.Preview.VersionId, job.Preview.Id, artifactId, Path.GetExtension(claim.File)[1..]);
            var target = paths.ArtifactPath(key);
            File.Move(source, target);
            rows.Add(new ArtifactRow(artifactId, job.Preview.Id, kind, key, claim.ContentType, size, HashFile(target), claim.PageNumber, claim.Width, claim.Height));
        }

        return rows;
    }

    private List<ArtifactRow>? Reject(ClaimedJob job, string reason)
    {
        logger.LogWarning("Preview {PreviewId} output rejected: {Reason}.", job.Preview.Id, reason);
        return null;
    }

    private bool HasExpectedContent(string path, string contentType)
    {
        using var stream = File.OpenRead(path);
        switch (contentType)
        {
            case "image/png":
                Span<byte> png = stackalloc byte[8];
                return stream.ReadAtLeast(png, 8, throwOnEndOfStream: false) == 8 && png.SequenceEqual(PngMagic);
            case "image/jpeg":
                Span<byte> jpeg = stackalloc byte[3];
                return stream.ReadAtLeast(jpeg, 3, throwOnEndOfStream: false) == 3 && jpeg[0] == 0xFF && jpeg[1] == 0xD8 && jpeg[2] == 0xFF;
            default:
                try
                {
                    using var document = JsonDocument.Parse(stream, new JsonDocumentOptions { MaxDepth = 64 });
                    return PreviewManifestValidation.IsGeometryValid(document.RootElement, settings.Limits);
                }
                catch (JsonException)
                {
                    return false;
                }
        }
    }

    private static string HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    private async Task RecordAsync(ClaimedJob job, JobOutcome outcome, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        await using var unitOfWork = (PostgresUnitOfWork)await unitOfWorkFactory.BeginAsync(cancellationToken);
        // Fence before inserting artifacts, whose uniqueness constraints may already belong to a newer lease.
        var ownsLease = await unitOfWork.Command("SELECT id FROM rcs.document_preview WHERE id = @id AND lease_token = @token AND status = 'PROCESSING' FOR UPDATE")
            .With("id", job.Preview.Id).With("token", job.LeaseToken)
            .ListAsync(reader => reader.Uuid("id"), cancellationToken);
        if (ownsLease.Count == 0)
        {
            return;
        }

        bool recorded;
        if (outcome.Status == PreviewStatus.Ready)
        {
            foreach (var artifact in outcome.Artifacts)
            {
                await PreviewSql.InsertArtifactAsync(unitOfWork, artifact, cancellationToken);
            }

            recorded = await PreviewSql.CompleteReadyAsync(unitOfWork, job.Preview.Id, job.LeaseToken,
                outcome.Manifest?.PageCount, outcome.Manifest?.PagesRendered, now, cancellationToken);
        }
        else
        {
            var code = outcome.FailureCode ?? PreviewFailureCodes.WorkerFailed;
            var retry = outcome.Status == PreviewStatus.Failed && PreviewFailureCodes.IsTransient(code) && job.Preview.AttemptCount < job.Preview.MaxAttempts;
            var backoff = TimeSpan.FromSeconds(Math.Max(1, settings.RetryBackoffSeconds) * Math.Pow(2, Math.Max(0, job.Preview.AttemptCount - 1)));
            recorded = await PreviewSql.RecordFailureAsync(unitOfWork, job.Preview.Id, job.LeaseToken,
                retry ? PreviewStatus.Pending : outcome.Status, code, outcome.FailureMessage ?? PreviewMessages.For(code),
                retry ? now + backoff : now, now, cancellationToken);
        }

        if (!recorded)
        {
            // The lease was lost (the job was reclaimed after an overrun): the other worker's result stands.
            await unitOfWork.RollbackAsync(cancellationToken);
            logger.LogWarning("Preview {PreviewId}: lease lost before the result was recorded; the result is discarded.", job.Preview.Id);
            // Unreferenced derived files can be inspected later; another lease's artifacts must survive.

            return;
        }

        await unitOfWork.CommitAsync(cancellationToken);
        logger.LogInformation("Preview {PreviewId} ({Processor}) for version {VersionId}: {Status} {Code}",
            job.Preview.Id, job.Preview.Processor, job.Preview.VersionId, outcome.Status, outcome.FailureCode);
    }

    /// <summary>Keeps a worker message only if it is short plain prose: no paths, no markup, no control characters.</summary>
    public static string? SanitizeMessage(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return null;
        }

        var builder = new StringBuilder();
        foreach (var character in message.Trim())
        {
            if (builder.Length >= 200)
            {
                break;
            }

            if (char.IsAsciiLetterOrDigit(character) || character is ' ' or '.' or ',' or '(' or ')' or '-' or '\'')
            {
                builder.Append(character);
            }
            else
            {
                return null; // Anything unexpected — a slash, a quote, a bracket — and the fixed message is used instead.
            }
        }

        return builder.ToString();
    }

    private static string Bounded(string text) => text.Length <= 2000 ? text : text[..2000];
}
