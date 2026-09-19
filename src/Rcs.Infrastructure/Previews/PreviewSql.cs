using Npgsql;
using Rcs.Domain.Documents;
using Rcs.Domain.Vocabulary;
using Rcs.Infrastructure.Persistence;

namespace Rcs.Infrastructure.Previews;

internal sealed record PreviewRow(
    Guid Id,
    Guid VersionId,
    string SourceHash,
    PreviewStatus Status,
    PreviewType Type,
    string Processor,
    string ProcessorVersion,
    string SettingsKey,
    string? FailureCode,
    string? FailureMessage,
    int AttemptCount,
    int MaxAttempts,
    int ManualRetryCount,
    Guid? LeaseToken,
    int? PageCount,
    int? PagesRendered,
    DateTimeOffset RequestedAt,
    DateTimeOffset? CompletedAt,
    int RowVersion,
    bool HasThumbnail);

internal sealed record ArtifactRow(
    Guid Id,
    Guid PreviewId,
    PreviewArtifactKind Kind,
    string StorageKey,
    string ContentType,
    long SizeBytes,
    string Sha256,
    int? PageNumber,
    int? Width,
    int? Height);

/// <summary>A job this process now holds the lease on, with what it needs to stage the exact source bytes.</summary>
internal sealed record ClaimedJob(PreviewRow Preview, Guid LeaseToken, string ContentHash, string VolumeCode, string RelativePath, long ByteSize, string MimeType);

/// <summary>A new generation to insert.</summary>
internal sealed record NewGeneration(
    Guid Id,
    Guid VersionId,
    string SourceHash,
    PreviewStatus Status,
    PreviewType Type,
    string Processor,
    string ProcessorVersion,
    string SettingsKey,
    string? FailureCode,
    string? FailureMessage,
    int MaxAttempts,
    Guid? RequestedBy,
    DateTimeOffset Now);

/// <summary>
/// The preview queue in PostgreSQL (ARCHITECTURE.md §14.2: a job table in the database the application already has — no
/// broker). A claim is <c>FOR UPDATE SKIP LOCKED</c> plus a lease token: two workers never hold the same job, and a
/// crashed worker's job becomes claimable again when its lease expires. Every state change after a claim is fenced by
/// the token, so a worker that lost its lease cannot overwrite the result of the one that took over.
/// </summary>
internal static class PreviewSql
{
    private const string Columns = """
        p.id, p.document_version_id, p.source_content_hash, p.status, p.preview_type, p.processor, p.processor_version,
        p.settings_key, p.failure_code, p.failure_message, p.attempt_count, p.max_attempts, p.manual_retry_count,
        p.lease_token, p.page_count, p.pages_rendered, p.requested_at, p.completed_at, p.row_version,
        EXISTS (SELECT 1 FROM rcs.document_preview_artifact AS t WHERE t.document_preview_id = p.id AND t.artifact_kind = 'THUMBNAIL') AS has_thumbnail
        """;

    public static PreviewRow Map(NpgsqlDataReader reader) => new(
        reader.Uuid("id"),
        reader.Uuid("document_version_id"),
        reader.Text("source_content_hash"),
        VocabularyCodes.FromCode<PreviewStatus>(reader.Text("status")),
        VocabularyCodes.FromCode<PreviewType>(reader.Text("preview_type")),
        reader.Text("processor"),
        reader.Text("processor_version"),
        reader.Text("settings_key"),
        reader.TextOrNull("failure_code"),
        reader.TextOrNull("failure_message"),
        reader.Int("attempt_count"),
        reader.Int("max_attempts"),
        reader.Int("manual_retry_count"),
        reader.UuidOrNull("lease_token"),
        reader.IsDBNull(reader.GetOrdinal("page_count")) ? null : reader.Int("page_count"),
        reader.IsDBNull(reader.GetOrdinal("pages_rendered")) ? null : reader.Int("pages_rendered"),
        reader.Instant("requested_at"),
        reader.InstantOrNull("completed_at"),
        reader.Int("row_version"),
        reader.Bool("has_thumbnail"));

    public static Task<PreviewRow?> CurrentAsync(PostgresUnitOfWork unitOfWork, Guid versionId, CancellationToken cancellationToken) =>
        unitOfWork.Command($"SELECT {Columns} FROM rcs.document_preview AS p WHERE p.document_version_id = @version AND p.superseded_at IS NULL")
            .With("version", versionId)
            .SingleOrDefaultAsync(Map, cancellationToken);

    public static Task<PreviewRow?> LoadAsync(PostgresUnitOfWork unitOfWork, Guid previewId, CancellationToken cancellationToken) =>
        unitOfWork.Command($"SELECT {Columns} FROM rcs.document_preview AS p WHERE p.id = @id")
            .With("id", previewId)
            .SingleOrDefaultAsync(Map, cancellationToken);

    public static Task<List<PreviewRow>> CurrentForVersionsAsync(PostgresUnitOfWork unitOfWork, IReadOnlyCollection<Guid> versionIds, CancellationToken cancellationToken) =>
        unitOfWork.Command($"SELECT {Columns} FROM rcs.document_preview AS p WHERE p.document_version_id = ANY(@versions) AND p.superseded_at IS NULL")
            .WithIds("versions", versionIds)
            .ListAsync(Map, cancellationToken);

    public static Task<List<PreviewRow>> ListAsync(PostgresUnitOfWork unitOfWork, IReadOnlyCollection<string> statuses, int limit, CancellationToken cancellationToken) =>
        unitOfWork.Command($"""
                SELECT {Columns} FROM rcs.document_preview AS p
                WHERE p.superseded_at IS NULL AND p.status = ANY(@statuses)
                ORDER BY p.requested_at DESC, p.id LIMIT @limit
                """)
            .WithTextArray("statuses", statuses)
            .With("limit", limit)
            .ListAsync(Map, cancellationToken);

    public static Task<List<ArtifactRow>> ArtifactsAsync(PostgresUnitOfWork unitOfWork, Guid previewId, CancellationToken cancellationToken) =>
        unitOfWork.Command("""
                SELECT id, document_preview_id, artifact_kind, storage_key, content_type, size_bytes, sha256, page_number, width, height
                FROM rcs.document_preview_artifact WHERE document_preview_id = @preview
                ORDER BY artifact_kind, page_number NULLS FIRST
                """)
            .With("preview", previewId)
            .ListAsync(MapArtifact, cancellationToken);

    public static ArtifactRow MapArtifact(NpgsqlDataReader reader) => new(
        reader.Uuid("id"),
        reader.Uuid("document_preview_id"),
        VocabularyCodes.FromCode<PreviewArtifactKind>(reader.Text("artifact_kind")),
        reader.Text("storage_key"),
        reader.Text("content_type"),
        reader.Long("size_bytes"),
        reader.Text("sha256"),
        reader.IsDBNull(reader.GetOrdinal("page_number")) ? null : reader.Int("page_number"),
        reader.IsDBNull(reader.GetOrdinal("width")) ? null : reader.Int("width"),
        reader.IsDBNull(reader.GetOrdinal("height")) ? null : reader.Int("height"));

    /// <summary>Versions with no current generation: new uploads, and everything uploaded before previews existed.</summary>
    public static Task<List<(Guid Id, string Hash, string MimeType)>> VersionsWithoutPreviewAsync(PostgresUnitOfWork unitOfWork, int batch, CancellationToken cancellationToken) =>
        unitOfWork.Command("""
                SELECT v.id, v.content_hash, v.mime_type FROM rcs.document_version AS v
                WHERE NOT EXISTS (SELECT 1 FROM rcs.document_preview AS p WHERE p.document_version_id = v.id AND p.superseded_at IS NULL)
                ORDER BY v.uploaded_at DESC, v.id LIMIT @batch
                """)
            .With("batch", batch)
            .ListAsync(reader => (reader.Uuid("id"), reader.Text("content_hash"), reader.Text("mime_type")), cancellationToken);

    /// <summary>Current UNSUPPORTED generations whose reason may have changed (a converter installed, a policy relaxed).</summary>
    public static Task<List<(Guid PreviewId, Guid VersionId, string Hash, string MimeType, string Processor)>> ReconsiderableAsync(PostgresUnitOfWork unitOfWork, int batch, CancellationToken cancellationToken) =>
        unitOfWork.Command("""
                SELECT p.id, v.id AS version_id, v.content_hash, v.mime_type, p.processor
                FROM rcs.document_preview AS p JOIN rcs.document_version AS v ON v.id = p.document_version_id
                WHERE p.superseded_at IS NULL AND p.status = 'UNSUPPORTED' AND p.failure_code IN ('PROCESSOR_UNAVAILABLE', 'EXCLUDED_BY_POLICY')
                ORDER BY p.requested_at LIMIT @batch
                """)
            .With("batch", batch)
            .ListAsync(reader => (reader.Uuid("id"), reader.Uuid("version_id"), reader.Text("content_hash"), reader.Text("mime_type"), reader.Text("processor")), cancellationToken);

    public static async Task<(Guid Id, string Hash, string MimeType)?> VersionAsync(PostgresUnitOfWork unitOfWork, Guid versionId, CancellationToken cancellationToken)
    {
        var rows = await unitOfWork.Command("SELECT id, content_hash, mime_type FROM rcs.document_version WHERE id = @id")
            .With("id", versionId)
            .ListAsync(reader => (Id: reader.Uuid("id"), Hash: reader.Text("content_hash"), MimeType: reader.Text("mime_type")), cancellationToken);
        return rows.Count == 1 ? rows[0] : null;
    }

    /// <summary>Inserts a generation unless the version already has a current one (another process got there first).</summary>
    public static async Task<bool> InsertAsync(PostgresUnitOfWork unitOfWork, NewGeneration generation, CancellationToken cancellationToken)
    {
        var settled = generation.Status is PreviewStatus.Unsupported or PreviewStatus.Failed;
        var inserted = await unitOfWork.Command("""
                INSERT INTO rcs.document_preview (
                    id, document_version_id, source_content_hash, preview_type, status, processor, processor_version, settings_key,
                    requested_at, requested_by_user_id, completed_at, failure_code, failure_message, attempt_count, max_attempts,
                    next_attempt_at)
                VALUES (
                    @id, @version, @hash, @type, @status, @processor, @processor_version, @settings,
                    @now, @requested_by, @completed, @failure_code, @failure_message, 0, @max_attempts, @now)
                ON CONFLICT (document_version_id) WHERE superseded_at IS NULL DO NOTHING
                """)
            .With("id", generation.Id)
            .With("version", generation.VersionId)
            .With("hash", generation.SourceHash)
            .With("type", generation.Type.ToCode())
            .With("status", generation.Status.ToCode())
            .With("processor", generation.Processor)
            .With("processor_version", generation.ProcessorVersion)
            .With("settings", generation.SettingsKey)
            .With("now", generation.Now)
            .With("requested_by", generation.RequestedBy)
            .With("completed", settled ? generation.Now : null)
            .With("failure_code", generation.FailureCode)
            .With("failure_message", generation.FailureMessage)
            .With("max_attempts", generation.MaxAttempts)
            .ExecuteAsync(cancellationToken);
        return inserted == 1;
    }

    /// <summary>Retires a settled current generation; a running one is never superseded (the CHECK refuses it too).</summary>
    public static async Task<bool> SupersedeAsync(PostgresUnitOfWork unitOfWork, Guid previewId, DateTimeOffset now, CancellationToken cancellationToken) =>
        await unitOfWork.Command("""
                UPDATE rcs.document_preview
                SET superseded_at = @now, updated_at = @now, row_version = row_version + 1
                WHERE id = @id AND superseded_at IS NULL AND status IN ('READY', 'FAILED', 'UNSUPPORTED')
                """)
            .With("id", previewId)
            .With("now", now)
            .ExecuteAsync(cancellationToken) == 1;

    /// <summary>A crashed worker's job whose attempt budget is spent: it will not be retried, so it is FAILED now.</summary>
    public static Task<int> FailExhaustedLeasesAsync(PostgresUnitOfWork unitOfWork, DateTimeOffset now, CancellationToken cancellationToken) =>
        unitOfWork.Command("""
                UPDATE rcs.document_preview
                SET status = 'FAILED', failure_code = 'WORKER_FAILED', failure_message = 'The preview worker stopped before finishing.',
                    completed_at = @now, lease_token = NULL, lease_expires_at = NULL, updated_at = @now, row_version = row_version + 1
                WHERE status = 'PROCESSING' AND lease_expires_at < @now AND attempt_count >= max_attempts
                """)
            .With("now", now)
            .ExecuteAsync(cancellationToken);

    /// <summary>
    /// Claims one due job — pending and due, or processing with an expired lease (its worker crashed) — for this
    /// process alone. SKIP LOCKED makes concurrent claimers pass over each other's row instead of waiting for it.
    /// </summary>
    public static async Task<ClaimedJob?> ClaimAsync(PostgresUnitOfWork unitOfWork, Guid token, DateTimeOffset now, TimeSpan lease, CancellationToken cancellationToken)
    {
        var claimed = await unitOfWork.Command($"""
                UPDATE rcs.document_preview AS p
                SET status = 'PROCESSING', lease_token = @token, lease_expires_at = @expires, started_at = @now,
                    attempt_count = p.attempt_count + 1, updated_at = @now, row_version = p.row_version + 1
                WHERE p.id = (
                    SELECT q.id FROM rcs.document_preview AS q
                    WHERE q.superseded_at IS NULL AND q.attempt_count < q.max_attempts
                      AND ((q.status = 'PENDING' AND q.next_attempt_at <= @now) OR (q.status = 'PROCESSING' AND q.lease_expires_at < @now))
                    ORDER BY q.next_attempt_at, q.id
                    FOR UPDATE SKIP LOCKED
                    LIMIT 1)
                RETURNING {Columns}
                """)
            .With("token", token)
            .With("now", now)
            .With("expires", now + lease)
            .SingleOrDefaultAsync(Map, cancellationToken);
        if (claimed is null)
        {
            return null;
        }

        return await unitOfWork.Command("""
                SELECT content_hash, storage_volume_code, stored_relative_path, byte_size, mime_type
                FROM rcs.document_version WHERE id = @id
                """)
            .With("id", claimed.VersionId)
            .SingleOrDefaultAsync(reader => new ClaimedJob(
                claimed,
                token,
                reader.Text("content_hash"),
                reader.Text("storage_volume_code"),
                reader.Text("stored_relative_path"),
                reader.Long("byte_size"),
                reader.Text("mime_type")), cancellationToken);
    }

    public static Task InsertArtifactAsync(PostgresUnitOfWork unitOfWork, ArtifactRow artifact, CancellationToken cancellationToken) =>
        unitOfWork.Command("""
                INSERT INTO rcs.document_preview_artifact (
                    id, document_preview_id, artifact_kind, storage_key, content_type, size_bytes, sha256, page_number, width, height)
                VALUES (@id, @preview, @kind, @key, @type, @size, @sha, @page, @width, @height)
                """)
            .With("id", artifact.Id)
            .With("preview", artifact.PreviewId)
            .With("kind", artifact.Kind.ToCode())
            .With("key", artifact.StorageKey)
            .With("type", artifact.ContentType)
            .WithLong("size", artifact.SizeBytes)
            .With("sha", artifact.Sha256)
            .With("page", artifact.PageNumber)
            .With("width", artifact.Width)
            .With("height", artifact.Height)
            .ExecuteAsync(cancellationToken);

    /// <summary>
    /// READY, fenced by the lease — and only with at least one artifact recorded in this same transaction, so a READY
    /// generation without artifacts cannot be committed.
    /// </summary>
    public static async Task<bool> CompleteReadyAsync(PostgresUnitOfWork unitOfWork, Guid previewId, Guid token, int? pageCount, int? pagesRendered, DateTimeOffset now, CancellationToken cancellationToken) =>
        await unitOfWork.Command("""
                UPDATE rcs.document_preview AS p
                SET status = 'READY', completed_at = @now, failure_code = NULL, failure_message = NULL, lease_token = NULL,
                    lease_expires_at = NULL, page_count = @pages, pages_rendered = @rendered, updated_at = @now, row_version = p.row_version + 1
                WHERE p.id = @id AND p.lease_token = @token AND p.status = 'PROCESSING'
                  AND EXISTS (SELECT 1 FROM rcs.document_preview_artifact AS a WHERE a.document_preview_id = p.id)
                """)
            .With("id", previewId)
            .With("token", token)
            .With("pages", pageCount)
            .With("rendered", pagesRendered)
            .With("now", now)
            .ExecuteAsync(cancellationToken) == 1;

    /// <summary>A failure, fenced by the lease: back to PENDING for a later attempt, or settled as FAILED / UNSUPPORTED.</summary>
    public static async Task<bool> RecordFailureAsync(
        PostgresUnitOfWork unitOfWork,
        Guid previewId,
        Guid token,
        PreviewStatus status,
        string code,
        string message,
        DateTimeOffset nextAttemptAt,
        DateTimeOffset now,
        CancellationToken cancellationToken) =>
        await unitOfWork.Command("""
                UPDATE rcs.document_preview AS p
                SET status = @status,
                    preview_type = CASE WHEN @status = 'UNSUPPORTED' THEN 'NONE' ELSE p.preview_type END,
                    failure_code = @code, failure_message = @message,
                    completed_at = CASE WHEN @status = 'PENDING' THEN NULL ELSE @now END,
                    next_attempt_at = @next, lease_token = NULL, lease_expires_at = NULL,
                    updated_at = @now, row_version = p.row_version + 1
                WHERE p.id = @id AND p.lease_token = @token AND p.status = 'PROCESSING'
                """)
            .With("id", previewId)
            .With("token", token)
            .With("status", status.ToCode())
            .With("code", code)
            .With("message", message)
            .With("next", nextAttemptAt)
            .With("now", now)
            .ExecuteAsync(cancellationToken) == 1;

    /// <summary>FAILED → PENDING with a fresh attempt budget. <paramref name="manualLimit"/> bounds user retries; null = operator.</summary>
    public static async Task<bool> RetryAsync(PostgresUnitOfWork unitOfWork, Guid previewId, Guid? requestedBy, int? manualLimit, DateTimeOffset now, CancellationToken cancellationToken) =>
        await unitOfWork.Command("""
                UPDATE rcs.document_preview
                SET status = 'PENDING', attempt_count = 0, next_attempt_at = @now, completed_at = NULL, failure_code = NULL,
                    failure_message = NULL, requested_by_user_id = COALESCE(@user, requested_by_user_id),
                    manual_retry_count = manual_retry_count + CASE WHEN @user IS NULL THEN 0 ELSE 1 END,
                    updated_at = @now, row_version = row_version + 1
                WHERE id = @id AND status = 'FAILED' AND superseded_at IS NULL
                  AND (@limit::integer IS NULL OR manual_retry_count < @limit::integer)
                """)
            .With("id", previewId)
            .With("user", requestedBy)
            .With("limit", manualLimit)
            .With("now", now)
            .ExecuteAsync(cancellationToken) == 1;
}
