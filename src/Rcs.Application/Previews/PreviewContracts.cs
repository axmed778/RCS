using Rcs.Application.Common;
using Rcs.Domain.Documents;

namespace Rcs.Application.Previews;

/// <summary>The current preview generation of one version, as a list row shows it.</summary>
/// <param name="CanRetry">A failed generation the actor may ask to try again (bounded per generation).</param>
public sealed record PreviewSummary(
    Guid PreviewId,
    PreviewStatus Status,
    PreviewType Type,
    string? FailureCode,
    bool HasThumbnail,
    bool CanRetry,
    int? PageCount,
    int? PagesRendered);

/// <summary>One artifact the viewer may request. Storage keys, paths and hashes never leave the application.</summary>
public sealed record PreviewArtifactView(Guid Id, PreviewArtifactKind Kind, string ContentType, int? PageNumber, int? Width, int? Height, long SizeBytes);

/// <summary>Everything the viewer needs for one exact version, reached through one placement.</summary>
public sealed record PreviewDetails(
    PreviewSummary Summary,
    string Title,
    string FileName,
    int VersionNo,
    long ByteSize,
    FileFamily Family,
    string Processor,
    string ProcessorVersion,
    DateTimeOffset? CompletedAt,
    IReadOnlyList<PreviewArtifactView> Artifacts);

/// <summary>An authorized artifact: a readable stream of derived bytes and the type they were validated as.</summary>
public sealed class PreviewArtifactContent(Stream content, string contentType, long sizeBytes, string fileName) : IAsyncDisposable
{
    public Stream Content { get; } = content;

    public string ContentType { get; } = contentType;

    public long SizeBytes { get; } = sizeBytes;

    public string FileName { get; } = fileName;

    public ValueTask DisposeAsync() => Content.DisposeAsync();
}

/// <summary>
/// Preview reads for users. Every method runs the same per-request, per-version check as a download
/// (DOCUMENT_MODEL.md §10.1; ADR-015): the placement must be ACTIVE, lie in the named case, expose exactly this version,
/// and the case must be visible to the actor now. A refusal and an unknown identifier look the same (NotFound).
/// </summary>
public interface IDocumentPreviewQueries
{
    /// <summary>Summaries for versions already exposed to the actor in a visible case (the workspace list).</summary>
    Task<IReadOnlyDictionary<Guid, PreviewSummary>> GetSummariesAsync(ActorContext actor, Guid caseId, IReadOnlyCollection<Guid> versionIds, CancellationToken cancellationToken = default);

    /// <summary>The status for polling. Not audited: it discloses nothing of the content.</summary>
    Task<CommandResult<PreviewSummary?>> GetStatusAsync(ActorContext actor, Guid caseId, Guid linkId, Guid versionId, CancellationToken cancellationToken = default);

    /// <summary>The viewer's data. Opening a READY preview is audited as PREVIEW, with the placement it went through.</summary>
    Task<CommandResult<PreviewDetails>> OpenAsync(ActorContext actor, Guid caseId, Guid linkId, Guid versionId, CancellationToken cancellationToken = default);

    /// <summary>One artifact of the version's current READY generation; any other artifact id is NotFound.</summary>
    Task<CommandResult<PreviewArtifactContent>> OpenArtifactAsync(ActorContext actor, Guid caseId, Guid linkId, Guid versionId, Guid artifactId, CancellationToken cancellationToken = default);

    Task<CommandResult<PreviewArtifactContent>> OpenThumbnailAsync(ActorContext actor, Guid caseId, Guid linkId, Guid versionId, CancellationToken cancellationToken = default);

    /// <summary>Queues a failed generation again, within the per-generation limit. Audited.</summary>
    Task<CommandResult<Guid>> RetryAsync(ActorContext actor, Guid caseId, Guid linkId, Guid versionId, CancellationToken cancellationToken = default);
}

/// <summary>One generation as an operator sees it (CLI). Contains no document content and no user-facing file names.</summary>
public sealed record PreviewAdminRow(
    Guid PreviewId,
    Guid DocumentVersionId,
    PreviewStatus Status,
    string Processor,
    string ProcessorVersion,
    int AttemptCount,
    int MaxAttempts,
    string? FailureCode,
    string? FailureMessage,
    DateTimeOffset RequestedAt,
    DateTimeOffset? CompletedAt);

public sealed record PreviewVerifyFinding(Guid PreviewId, Guid? ArtifactId, string Problem);

public sealed record PreviewVerifyReport(int GenerationsChecked, int ArtifactsChecked, IReadOnlyList<PreviewVerifyFinding> Findings);

/// <summary>Operator actions (the <c>preview-*</c> commands). Recorded as JOB audit events: no person is signed in.</summary>
public interface IPreviewAdministration
{
    Task<IReadOnlyList<PreviewAdminRow>> ListAsync(IReadOnlyCollection<PreviewStatus> statuses, int limit, CancellationToken cancellationToken = default);

    /// <summary>Queues a FAILED generation again with a fresh attempt budget.</summary>
    Task<CommandResult<Guid>> RetryAsync(Guid previewId, CancellationToken cancellationToken = default);

    /// <summary>Supersedes the version's current settled generation and queues a new one with the processors available now.</summary>
    Task<CommandResult<Guid>> RegenerateAsync(Guid documentVersionId, CancellationToken cancellationToken = default);

    /// <summary>Checks that every READY generation's artifacts exist with their recorded size and hash. Read-only.</summary>
    Task<PreviewVerifyReport> VerifyAsync(CancellationToken cancellationToken = default);
}

/// <summary>The background work: queue what is missing, then run one due job.</summary>
public interface IPreviewJobRunner
{
    /// <summary>Creates generations for versions that have none (new uploads, older documents). Returns how many.</summary>
    Task<int> ReconcileAsync(CancellationToken cancellationToken = default);

    /// <summary>Claims and runs one due job. False when there was nothing to do.</summary>
    Task<bool> RunNextAsync(CancellationToken cancellationToken = default);
}
