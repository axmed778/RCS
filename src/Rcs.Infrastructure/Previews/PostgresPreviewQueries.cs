using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Rcs.Application.Common;
using Rcs.Application.Identifiers;
using Rcs.Application.Identity;
using Rcs.Application.Persistence;
using Rcs.Application.Previews;
using Rcs.Domain.Authorization;
using Rcs.Domain.Documents;
using Rcs.Domain.Vocabulary;
using Rcs.Infrastructure.Audit;
using Rcs.Infrastructure.Cases;
using Rcs.Infrastructure.Documents;
using Rcs.Infrastructure.Identity;
using Rcs.Infrastructure.Persistence;

namespace Rcs.Infrastructure.Previews;

/// <summary>
/// Preview reads and retries for users. Every entry point first runs <see cref="DocumentVersionAccess"/> — the same
/// check a download runs — for the exact version in the URL, and then only ever touches that version's CURRENT
/// generation: an artifact id from another version, another generation, or nowhere is NotFound, exactly like a refusal.
/// </summary>
internal sealed class PostgresPreviewQueries(
    IUnitOfWorkFactory unitOfWorkFactory,
    PreviewPaths paths,
    AuditWriter audit,
    IIdGenerator ids,
    TimeProvider timeProvider,
    IOptions<PreviewOptions> options,
    ILogger<PostgresPreviewQueries> logger) : IDocumentPreviewQueries
{
    private readonly PreviewOptions settings = options.Value;

    private static CommandResult<T> NotFound<T>() => CommandResult<T>.Failure(CommandErrorKind.NotFound, "notfound.document");

    public async Task<IReadOnlyDictionary<Guid, PreviewSummary>> GetSummariesAsync(ActorContext actor, Guid caseId, IReadOnlyCollection<Guid> versionIds, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(versionIds);
        if (!settings.Enabled || versionIds.Count == 0)
        {
            return new Dictionary<Guid, PreviewSummary>();
        }

        await using var unitOfWork = (PostgresUnitOfWork)await unitOfWorkFactory.BeginAsync(cancellationToken);
        var now = timeProvider.GetUtcNow();
        var profile = await ActorStore.LoadAsync(unitOfWork, actor.UserId, now, cancellationToken);
        var caseRow = await CaseSql.LoadAsync(unitOfWork, caseId, cancellationToken);
        if (profile is null || caseRow is null)
        {
            return new Dictionary<Guid, PreviewSummary>();
        }

        var relationship = new CaseRelationship(caseRow.IsRestricted, await CaseSql.ActorIsAssignedAsync(unitOfWork, caseId, profile.UserId, now, cancellationToken));
        if (!AuthorizationPolicy.Decide(profile.Authority(), BusinessAction.ViewDocument, relationship).IsAllowed)
        {
            return new Dictionary<Guid, PreviewSummary>();
        }

        var exposed = new List<Guid>();
        foreach (var id in versionIds.Distinct().Take(1000))
        {
            var version = await DocumentSql.LoadVersionAsync(unitOfWork, id, cancellationToken);
            if (version is not null && (await DocumentSql.ActiveLinksOfDocumentAsync(unitOfWork, version.DocumentId, cancellationToken))
                .Any(link => link.ContextCaseId == caseId && DocumentSql.Exposes(link, version)))
            {
                exposed.Add(id);
            }
        }

        var rows = await PreviewSql.CurrentForVersionsAsync(unitOfWork, exposed, cancellationToken);
        return rows.ToDictionary(row => row.VersionId, Summarize);
    }

    public async Task<CommandResult<PreviewSummary?>> GetStatusAsync(ActorContext actor, Guid caseId, Guid linkId, Guid versionId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        await using var unitOfWork = (PostgresUnitOfWork)await unitOfWorkFactory.BeginAsync(cancellationToken);
        var access = await AuthorizeAsync(unitOfWork, actor, caseId, linkId, versionId, "PreviewStatus", cancellationToken);
        if (access is null)
        {
            return NotFound<PreviewSummary?>();
        }

        var current = await PreviewSql.CurrentAsync(unitOfWork, versionId, cancellationToken);
        return CommandResult<PreviewSummary?>.Success(current is null ? null : Summarize(current));
    }

    public async Task<CommandResult<PreviewDetails>> OpenAsync(ActorContext actor, Guid caseId, Guid linkId, Guid versionId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        await using var unitOfWork = (PostgresUnitOfWork)await unitOfWorkFactory.BeginAsync(cancellationToken);
        var access = await AuthorizeAsync(unitOfWork, actor, caseId, linkId, versionId, "Preview", cancellationToken);
        if (access is null)
        {
            return NotFound<PreviewDetails>();
        }

        var (profile, link, version) = (access.Profile, access.Link, access.Version);
        var document = (await DocumentSql.LoadDocumentAsync(unitOfWork, version.DocumentId, forUpdate: false, cancellationToken))!;
        var current = await PreviewSql.CurrentAsync(unitOfWork, versionId, cancellationToken);
        var artifacts = current is { Status: PreviewStatus.Ready } ? await PreviewSql.ArtifactsAsync(unitOfWork, current.Id, cancellationToken) : [];
        var summary = current is null
            ? new PreviewSummary(Guid.Empty, PreviewStatus.Pending, PreviewType.None, null, false, false, null, null)
            : Summarize(current);

        if (current is { Status: PreviewStatus.Ready })
        {
            // Opening a preview discloses the content of exactly this version, through exactly this placement (§13.3).
            await audit.WriteAsync(unitOfWork, profile, actor.ClientHost, ids.NewId(), new AuditEntry(
                AuditActionCodes.Preview, AuditEntityTypes.DocumentVersion, version.Id, version.RowVersion, link.ContextCaseId,
                After: new
                {
                    document_id = version.DocumentId,
                    version_no = version.VersionNo,
                    link_id = link.Id,
                    role = link.RoleCode,
                    target_type = DocumentSql.TargetEntityType(link.Target.Kind),
                    target_id = link.Target.Id,
                    preview_id = current.Id,
                    processor = current.Processor,
                    processor_version = current.ProcessorVersion,
                },
                DocumentHash: version.ContentHash), cancellationToken);
        }

        await unitOfWork.CommitAsync(cancellationToken);
        return CommandResult<PreviewDetails>.Success(new PreviewDetails(
            summary,
            document.Title,
            version.OriginalFileName,
            version.VersionNo,
            version.ByteSize,
            FileTypePolicy.FamilyOf(version.MimeType),
            current?.Processor ?? PreviewRouting.Route(version.MimeType, settings.AllowMacroEnabledOffice).Processor,
            current?.ProcessorVersion ?? string.Empty,
            current?.CompletedAt,
            artifacts.Select(artifact => new PreviewArtifactView(artifact.Id, artifact.Kind, artifact.ContentType, artifact.PageNumber, artifact.Width, artifact.Height, artifact.SizeBytes)).ToArray()));
    }

    public Task<CommandResult<PreviewArtifactContent>> OpenArtifactAsync(ActorContext actor, Guid caseId, Guid linkId, Guid versionId, Guid artifactId, CancellationToken cancellationToken = default) =>
        OpenArtifactCoreAsync(actor, caseId, linkId, versionId, artifacts => artifacts.FirstOrDefault(artifact => artifact.Id == artifactId), cancellationToken);

    public Task<CommandResult<PreviewArtifactContent>> OpenThumbnailAsync(ActorContext actor, Guid caseId, Guid linkId, Guid versionId, CancellationToken cancellationToken = default) =>
        OpenArtifactCoreAsync(actor, caseId, linkId, versionId, artifacts => artifacts.FirstOrDefault(artifact => artifact.Kind == PreviewArtifactKind.Thumbnail), cancellationToken);

    public async Task<CommandResult<Guid>> RetryAsync(ActorContext actor, Guid caseId, Guid linkId, Guid versionId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        await using var unitOfWork = (PostgresUnitOfWork)await unitOfWorkFactory.BeginAsync(cancellationToken);
        var access = await AuthorizeAsync(unitOfWork, actor, caseId, linkId, versionId, "PreviewRetry", cancellationToken);
        if (access is null)
        {
            return NotFound<Guid>();
        }

        var current = await PreviewSql.CurrentAsync(unitOfWork, versionId, cancellationToken);
        var now = timeProvider.GetUtcNow();
        if (current is null || !await PreviewSql.RetryAsync(unitOfWork, current.Id, access.Profile.UserId, Math.Max(0, settings.ManualRetryLimit), now, cancellationToken))
        {
            return CommandResult<Guid>.Failure(CommandErrorKind.Conflict, "preview.retry_not_allowed");
        }

        await audit.WriteAsync(unitOfWork, access.Profile, actor.ClientHost, ids.NewId(), new AuditEntry(
            AuditActionCodes.Update, AuditEntityTypes.DocumentPreview, current.Id, current.RowVersion + 1, access.Link.ContextCaseId,
            Before: new { status = current.Status.ToCode(), failure_code = current.FailureCode },
            After: new { status = PreviewStatus.Pending.ToCode(), document_version_id = versionId, manual_retry = current.ManualRetryCount + 1 }), cancellationToken);
        await unitOfWork.CommitAsync(cancellationToken);
        return CommandResult<Guid>.Success(current.Id);
    }

    private async Task<CommandResult<PreviewArtifactContent>> OpenArtifactCoreAsync(
        ActorContext actor, Guid caseId, Guid linkId, Guid versionId, Func<List<ArtifactRow>, ArtifactRow?> pick, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        await using var unitOfWork = (PostgresUnitOfWork)await unitOfWorkFactory.BeginAsync(cancellationToken);
        var access = await AuthorizeAsync(unitOfWork, actor, caseId, linkId, versionId, "PreviewArtifact", cancellationToken);
        if (access is null)
        {
            return NotFound<PreviewArtifactContent>();
        }

        var current = await PreviewSql.CurrentAsync(unitOfWork, versionId, cancellationToken);
        var artifact = current is { Status: PreviewStatus.Ready } ? pick(await PreviewSql.ArtifactsAsync(unitOfWork, current.Id, cancellationToken)) : null;
        if (artifact is null)
        {
            return NotFound<PreviewArtifactContent>();
        }

        var path = paths.ArtifactPath(artifact.StorageKey);
        var info = new FileInfo(path);
        if (!info.Exists || info.LinkTarget is not null || info.Length != artifact.SizeBytes)
        {
            // Derived cache went missing or changed: an operator regenerates it; the original is unaffected.
            logger.LogError("Preview artifact {ArtifactId} of generation {PreviewId} is missing or has the wrong size.", artifact.Id, current!.Id);
            return NotFound<PreviewArtifactContent>();
        }

        var name = artifact.Kind switch
        {
            PreviewArtifactKind.Page => $"page-{artifact.PageNumber}",
            PreviewArtifactKind.Thumbnail => "thumbnail",
            PreviewArtifactKind.Geometry => "geometry",
            _ => "preview",
        } + Path.GetExtension(artifact.StorageKey);
        var content = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        try
        {
            var hash = Convert.ToHexStringLower(await System.Security.Cryptography.SHA256.HashDataAsync(content, cancellationToken));
            if (!string.Equals(hash, artifact.Sha256, StringComparison.Ordinal))
            {
                await content.DisposeAsync();
                logger.LogError("Preview artifact {ArtifactId} failed its integrity check.", artifact.Id);
                return NotFound<PreviewArtifactContent>();
            }
            content.Position = 0;
        }
        catch
        {
            await content.DisposeAsync();
            throw;
        }
        return CommandResult<PreviewArtifactContent>.Success(new PreviewArtifactContent(
            content,
            artifact.ContentType,
            artifact.SizeBytes,
            name));
    }

    /// <summary>The shared version check; a refusal is audited and committed here, and returned as null.</summary>
    private async Task<VersionAccess?> AuthorizeAsync(PostgresUnitOfWork unitOfWork, ActorContext actor, Guid caseId, Guid linkId, Guid versionId, string attempted, CancellationToken cancellationToken)
    {
        if (!settings.Enabled)
        {
            return null;
        }

        var result = await DocumentVersionAccess.AuthorizeAsync(unitOfWork, audit, ids, actor, timeProvider.GetUtcNow(), caseId, linkId, versionId, attempted, cancellationToken);
        if (result.DenialRecorded)
        {
            await unitOfWork.CommitAsync(cancellationToken);
        }

        return result.Access;
    }

    private PreviewSummary Summarize(PreviewRow row) => new(
        row.Id,
        row.Status,
        row.Type,
        row.FailureCode,
        row.HasThumbnail && row.Status == PreviewStatus.Ready,
        row.Status == PreviewStatus.Failed && row.ManualRetryCount < settings.ManualRetryLimit,
        row.PageCount,
        row.PagesRendered);
}

/// <summary>The operator commands. No person is signed in, so changes are JOB audit events.</summary>
internal sealed class PostgresPreviewAdministration(
    IUnitOfWorkFactory unitOfWorkFactory,
    PreviewPaths paths,
    PreviewPlanner planner,
    AuditWriter audit,
    IIdGenerator ids,
    TimeProvider timeProvider) : IPreviewAdministration
{
    public async Task<IReadOnlyList<PreviewAdminRow>> ListAsync(IReadOnlyCollection<PreviewStatus> statuses, int limit, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(statuses);
        await using var unitOfWork = (PostgresUnitOfWork)await unitOfWorkFactory.BeginAsync(cancellationToken);
        var rows = await PreviewSql.ListAsync(unitOfWork, statuses.Select(status => status.ToCode()).ToArray(), Math.Clamp(limit, 1, 10_000), cancellationToken);
        return rows.Select(row => new PreviewAdminRow(row.Id, row.VersionId, row.Status, row.Processor, row.ProcessorVersion, row.AttemptCount,
            row.MaxAttempts, row.FailureCode, row.FailureMessage, row.RequestedAt, row.CompletedAt)).ToArray();
    }

    public async Task<CommandResult<Guid>> RetryAsync(Guid previewId, CancellationToken cancellationToken = default)
    {
        await using var unitOfWork = (PostgresUnitOfWork)await unitOfWorkFactory.BeginAsync(cancellationToken);
        var now = timeProvider.GetUtcNow();
        var row = await PreviewSql.LoadAsync(unitOfWork, previewId, cancellationToken);
        if (row is null)
        {
            return CommandResult<Guid>.Failure(CommandErrorKind.NotFound, "notfound.preview");
        }

        if (!await PreviewSql.RetryAsync(unitOfWork, previewId, null, null, now, cancellationToken))
        {
            return CommandResult<Guid>.Failure(CommandErrorKind.Conflict, "preview.retry_not_allowed");
        }

        await audit.WriteJobAsync(unitOfWork, ids.NewId(), new AuditEntry(
            AuditActionCodes.Update, AuditEntityTypes.DocumentPreview, previewId, row.RowVersion + 1, null,
            Before: new { status = row.Status.ToCode(), failure_code = row.FailureCode },
            After: new { status = PreviewStatus.Pending.ToCode(), document_version_id = row.VersionId, operator_retry = true }), cancellationToken);
        await unitOfWork.CommitAsync(cancellationToken);
        return CommandResult<Guid>.Success(previewId);
    }

    public async Task<CommandResult<Guid>> RegenerateAsync(Guid documentVersionId, CancellationToken cancellationToken = default)
    {
        await planner.EnsureCapabilitiesAsync(cancellationToken);
        await using var unitOfWork = (PostgresUnitOfWork)await unitOfWorkFactory.BeginAsync(cancellationToken);
        var now = timeProvider.GetUtcNow();
        var version = await PreviewSql.VersionAsync(unitOfWork, documentVersionId, cancellationToken);
        if (version is null)
        {
            return CommandResult<Guid>.Failure(CommandErrorKind.NotFound, "notfound.document");
        }

        var current = await PreviewSql.CurrentAsync(unitOfWork, documentVersionId, cancellationToken);
        if (current is { Status: PreviewStatus.Pending or PreviewStatus.Processing })
        {
            return CommandResult<Guid>.Failure(CommandErrorKind.Conflict, "preview.in_progress");
        }

        var generation = planner.Plan(ids.NewId(), version.Value.Id, version.Value.Hash, version.Value.MimeType, null, now);
        if (generation is null)
        {
            return CommandResult<Guid>.Failure(CommandErrorKind.RuleViolation, "preview.capabilities_unknown");
        }

        if (current is not null && !await PreviewSql.SupersedeAsync(unitOfWork, current.Id, now, cancellationToken))
        {
            return CommandResult<Guid>.Failure(CommandErrorKind.Conflict, "preview.in_progress");
        }

        if (!await PreviewSql.InsertAsync(unitOfWork, generation, cancellationToken))
        {
            return CommandResult<Guid>.Failure(CommandErrorKind.Conflict, "preview.in_progress");
        }

        await audit.WriteJobAsync(unitOfWork, ids.NewId(), new AuditEntry(
            AuditActionCodes.Create, AuditEntityTypes.DocumentPreview, generation.Id, 1, null,
            Before: current is null ? null : new { superseded_preview_id = current.Id, status = current.Status.ToCode(), processor_version = current.ProcessorVersion },
            After: new { document_version_id = documentVersionId, status = generation.Status.ToCode(), processor = generation.Processor, processor_version = generation.ProcessorVersion }), cancellationToken);
        await unitOfWork.CommitAsync(cancellationToken);
        return CommandResult<Guid>.Success(generation.Id);
    }

    public async Task<PreviewVerifyReport> VerifyAsync(CancellationToken cancellationToken = default)
    {
        await using var unitOfWork = (PostgresUnitOfWork)await unitOfWorkFactory.BeginAsync(cancellationToken);
        var ready = await PreviewSql.ListAsync(unitOfWork, [PreviewStatus.Ready.ToCode()], int.MaxValue, cancellationToken);
        var findings = new List<PreviewVerifyFinding>();
        var checkedArtifacts = 0;
        foreach (var generation in ready)
        {
            var artifacts = await PreviewSql.ArtifactsAsync(unitOfWork, generation.Id, cancellationToken);
            if (artifacts.Count == 0)
            {
                findings.Add(new PreviewVerifyFinding(generation.Id, null, "READY generation has no artifacts"));
            }

            foreach (var artifact in artifacts)
            {
                checkedArtifacts++;
                var path = paths.ArtifactPath(artifact.StorageKey);
                if (!File.Exists(path))
                {
                    findings.Add(new PreviewVerifyFinding(generation.Id, artifact.Id, "artifact file is missing"));
                    continue;
                }

                await using var stream = File.OpenRead(path);
                if (stream.Length != artifact.SizeBytes)
                {
                    findings.Add(new PreviewVerifyFinding(generation.Id, artifact.Id, "artifact size differs from its record"));
                    continue;
                }

                var hash = Convert.ToHexStringLower(await System.Security.Cryptography.SHA256.HashDataAsync(stream, cancellationToken));
                if (!string.Equals(hash, artifact.Sha256, StringComparison.Ordinal))
                {
                    findings.Add(new PreviewVerifyFinding(generation.Id, artifact.Id, "artifact hash differs from its record"));
                }
            }
        }

        return new PreviewVerifyReport(ready.Count, checkedArtifacts, findings);
    }
}
