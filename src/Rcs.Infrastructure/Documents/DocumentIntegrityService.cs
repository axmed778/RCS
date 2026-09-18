using Microsoft.Extensions.Logging;
using Rcs.Application.Documents;
using Rcs.Application.Identifiers;
using Rcs.Application.Persistence;
using Rcs.Domain.Vocabulary;
using Rcs.Infrastructure.Audit;
using Rcs.Infrastructure.Persistence;

namespace Rcs.Infrastructure.Documents;

/// <summary>
/// The minimum integrity check of DOCUMENT_MODEL.md §11: detection, never repair. Both halves are only read; the one
/// write is the <c>integrity_checked_at</c> stamp on a version that fully verified, and a JOB audit event per critical
/// finding (§13.2). A finding changes no business record — the version stays exactly as it was (§11.4).
/// </summary>
/// <remarks>Scheduling (nightly existence/size, rolling re-hash) is deferred; this is the capability the job will call.</remarks>
internal sealed class DocumentIntegrityService(
    IUnitOfWorkFactory unitOfWorkFactory,
    LocalContentStore store,
    AuditWriter audit,
    IIdGenerator ids,
    TimeProvider timeProvider,
    ILogger<DocumentIntegrityService> logger) : IDocumentIntegrityService
{
    public async Task<IntegrityReport> CheckAsync(bool rehash, bool scanForUnreferenced, CancellationToken cancellationToken = default)
    {
        List<(Guid Id, string Volume, string Path, long Size, string Hash)> versions;
        await using (var read = (PostgresUnitOfWork)await unitOfWorkFactory.BeginAsync(cancellationToken))
        {
            versions = await read.Command("""
                    SELECT id, storage_volume_code, stored_relative_path, byte_size, content_hash
                    FROM rcs.document_version ORDER BY integrity_checked_at NULLS FIRST, id
                    """)
                .ListAsync(reader => (reader.Uuid("id"), reader.Text("storage_volume_code"), reader.Text("stored_relative_path"),
                    reader.Long("byte_size"), reader.Text("content_hash")), cancellationToken);
        }

        var findings = new List<IntegrityFinding>();
        var verified = new List<Guid>();
        foreach (var version in versions)
        {
            var state = await store.CheckAsync(version.Volume, version.Path, version.Size, rehash ? version.Hash : null, cancellationToken);
            if (state == ObjectState.Unreadable)
            {
                // I5: retry once before reporting (§11.1).
                state = await store.CheckAsync(version.Volume, version.Path, version.Size, rehash ? version.Hash : null, cancellationToken);
            }

            var kind = state switch
            {
                ObjectState.Missing => IntegrityFindingKind.MissingObject,
                ObjectState.SizeMismatch => IntegrityFindingKind.SizeMismatch,
                ObjectState.HashMismatch => IntegrityFindingKind.HashMismatch,
                ObjectState.Unreadable => IntegrityFindingKind.Unreadable,
                _ => (IntegrityFindingKind?)null,
            };

            if (kind is { } found)
            {
                findings.Add(new IntegrityFinding(found, version.Id, version.Path, state.ToString()));
            }
            else if (rehash)
            {
                verified.Add(version.Id);
            }
        }

        var objectsScanned = 0;
        if (scanForUnreferenced)
        {
            var referenced = versions.Select(version => version.Path).ToHashSet(StringComparer.Ordinal);
            foreach (var path in store.EnumerateObjects())
            {
                objectsScanned++;
                if (!referenced.Contains(path))
                {
                    // I2: informational. Retained — V1 performs no physical garbage collection (ADR-026, ADR-043).
                    findings.Add(new IntegrityFinding(IntegrityFindingKind.UnreferencedObject, null, path, "no version references this object"));
                }
            }
        }

        await RecordAsync(findings, verified, cancellationToken);
        return new IntegrityReport(versions.Count, objectsScanned, findings);
    }

    private async Task RecordAsync(IReadOnlyList<IntegrityFinding> findings, IReadOnlyList<Guid> verified, CancellationToken cancellationToken)
    {
        await using var unitOfWork = (PostgresUnitOfWork)await unitOfWorkFactory.BeginAsync(cancellationToken);
        var correlationId = ids.NewId();
        foreach (var finding in findings.Where(finding => finding.VersionId is not null))
        {
            logger.LogCritical("Document integrity finding {Kind} for version {VersionId} at {Path}.", finding.Kind, finding.VersionId, finding.StoragePath);
            var caseId = await unitOfWork.Command("""
                    SELECT ctx.context_case_id
                    FROM rcs.document_link AS l
                    JOIN rcs.document_link_context AS ctx ON ctx.document_link_id = l.id
                    JOIN rcs.document_version AS v ON v.document_id = l.document_id
                    WHERE v.id = @id AND l.is_origin
                    ORDER BY l.status, l.linked_at DESC
                    LIMIT 1
                    """)
                .With("id", finding.VersionId!.Value)
                .ScalarAsync<Guid?>(cancellationToken);
            await audit.WriteJobAsync(unitOfWork, correlationId, new AuditEntry(
                AuditActionCodes.Update, AuditEntityTypes.DocumentVersion, finding.VersionId.Value, null, caseId,
                After: new { integrity_finding = finding.Kind.ToString(), detail = finding.Detail }), cancellationToken);
        }

        if (verified.Count > 0)
        {
            await unitOfWork.Command("UPDATE rcs.document_version SET integrity_checked_at = @now WHERE id = ANY(@ids)")
                .With("now", timeProvider.GetUtcNow())
                .WithIds("ids", verified)
                .ExecuteAsync(cancellationToken);
        }

        await unitOfWork.CommitAsync(cancellationToken);
    }
}
