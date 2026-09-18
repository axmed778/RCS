using Rcs.Application.Cases;
using Rcs.Application.Common;
using Rcs.Application.Idempotency;
using Rcs.Domain.Documents;
using Rcs.Domain.Vocabulary;

namespace Rcs.Application.Documents;

/// <summary>The business object a file is placed in: one arm of the exclusive arc of <c>document_link</c>.</summary>
public sealed record DocumentTarget(DocumentTargetKind Kind, Guid Id);

/// <summary>
/// The bytes of one upload as the host received them: a forward-only stream that is read once, straight to the
/// temporary area of the object store. It is never buffered whole in memory (ARCHITECTURE.md §12.3).
/// </summary>
/// <param name="OriginalFileName">Exactly as supplied — metadata only, never a path and never identity (DOCUMENT_MODEL.md §5.5).</param>
/// <param name="DeclaredContentType">The client's claim, never trusted; a mismatch with the detected type is audited.</param>
public sealed record UploadedContent(Stream Content, string OriginalFileName, string? DeclaredContentType);

/// <summary>
/// A new logical document entering the system through one business context (DOCUMENT_MODEL.md §7.1). That placement is
/// its origin and fixes its home case. The operation id is generated once when the upload form is prepared, so a retry
/// after an uncertain response creates nothing twice (ADR-020).
/// </summary>
/// <param name="Float">
/// Ask for a floating placement. Honoured only where the model allows it — a SUPPORTING / WORKING_COPY working
/// placement in the home case (§4.4); everything else is pinned regardless.
/// </param>
/// <param name="Note">For REQUIREMENT_EVIDENCE: the evidence note written with the evidence row.</param>
public sealed record UploadDocumentCommand(
    OperationId OperationId,
    Guid CaseId,
    DocumentTarget Target,
    string RoleCode,
    string Title,
    string DocumentKindCode,
    string? Description,
    DateOnly? DocumentDate,
    string? DocumentReference,
    bool Float = false,
    string? Note = null);

/// <summary>
/// New bytes for an existing document (DOCUMENT_MODEL.md §7.5) — only through its home case. Identical bytes converge
/// on the existing version instead of creating a phantom one (§7.4).
/// </summary>
/// <param name="ViaLinkId">A placement of the document in its home case that the actor can see.</param>
/// <param name="Placement">
/// Where the new version is also placed, pinned — e.g. the later letter that introduced it. Required when no floating
/// placement would otherwise expose the new version to anyone.
/// </param>
public sealed record UploadVersionCommand(
    OperationId OperationId,
    Guid CaseId,
    Guid ViaLinkId,
    DateOnly? DocumentDate,
    DocumentTarget? Placement,
    string? PlacementRoleCode);

/// <summary>What an upload produced.</summary>
/// <param name="ConvergedOnExistingVersion">
/// The same bytes were already a version of this document: nothing new was stored or versioned. When that version is
/// not current, the way back is reinstatement, never a re-upload (ADR-027).
/// </param>
public sealed record UploadOutcome(Guid DocumentId, Guid VersionId, Guid? LinkId, bool ConvergedOnExistingVersion);

/// <summary>
/// Places a version the actor can already see into another context: an annex of an outgoing letter, the signed
/// decision of a final result, supporting material — or, into another case, an explicit disclosure (Chief, reason,
/// pinned; DOCUMENT_MODEL.md §4.8, ADR-016). Evidence has its own command because it writes two rows.
/// </summary>
/// <param name="CaseId">The case of the target context.</param>
/// <param name="SourceLinkId">A visible ACTIVE placement that exposes <paramref name="VersionId"/>.</param>
public sealed record PlaceVersionCommand(
    OperationId OperationId,
    Guid CaseId,
    Guid SourceLinkId,
    Guid VersionId,
    DocumentTarget Target,
    string RoleCode,
    string? Reason);

/// <summary>
/// Records one exact document version as evidence for a requirement: the <c>requirement_evidence</c> row and its pinned
/// <c>REQUIREMENT_EVIDENCE</c> placement in one transaction (DOCUMENT_MODEL.md §4.6).
/// </summary>
public sealed record RecordDocumentEvidenceCommand(
    OperationId OperationId,
    Guid CaseId,
    Guid RequirementId,
    Guid SourceLinkId,
    Guid VersionId,
    string? Note,
    string? DisclosureReason = null);

/// <summary>Evidence recorded in error: RETRACTED with a note, and its placement REMOVED with it. Nothing is deleted.</summary>
public sealed record RetractEvidenceCommand(Guid CaseId, Guid EvidenceId, int RowVersion, string Note);

/// <summary>A placement that should not be there (DOCUMENT_MODEL.md §8.2): REMOVED with a reason. The file is untouched.</summary>
public sealed record RemoveLinkCommand(Guid CaseId, Guid LinkId, int RowVersion, string Reason);

/// <summary>
/// The correct file in the wrong place, or in the wrong role (§8.2 cases 1, 2, 6): the old placement is REMOVED and a
/// new one created, pinned to the same version, under one correlation id. A move into another case is a disclosure
/// decision (Chief); moving the origin moves the home case.
/// </summary>
public sealed record MoveLinkCommand(Guid CaseId, Guid LinkId, int RowVersion, Guid TargetCaseId, DocumentTarget NewTarget, string NewRoleCode, string Reason);

/// <summary>
/// A version that should never have been uploaded, or was recalled (§5.2, §8.2 case 3): WITHDRAWN with a reason, bytes
/// retained. When it was the current version, the previous one may be reinstated in the same act (ADR-027).
/// </summary>
public sealed record WithdrawVersionCommand(
    Guid CaseId,
    Guid ViaLinkId,
    Guid VersionId,
    int RowVersion,
    string ReasonCode,
    string? Note,
    bool ReinstatePrevious,
    string? ReinstatementReason);

/// <summary>
/// A document that should not exist at all — a duplicate, or a wrong file entered as a new document (§8.2 cases 3, 4):
/// the document and its remaining versions WITHDRAWN, its placements REMOVED. Nothing is deleted.
/// </summary>
public sealed record WithdrawDocumentCommand(Guid CaseId, Guid ViaLinkId, int DocumentRowVersion, string ReasonCode, string? Note);

/// <summary>Makes the newest non-withdrawn version current again (ADR-027). The same row; nothing is re-uploaded.</summary>
public sealed record ReinstateVersionCommand(Guid CaseId, Guid ViaLinkId, Guid VersionId, int RowVersion, string Reason);

/// <summary>A wrong display title or metadata (§8.2 case 5). Visible wherever the document is placed.</summary>
public sealed record EditDocumentMetadataCommand(
    Guid CaseId,
    Guid ViaLinkId,
    int DocumentRowVersion,
    string Title,
    string DocumentKindCode,
    string? Description,
    string? DocumentReference);

public interface IDocumentService
{
    Task<CommandResult<UploadOutcome>> UploadDocumentAsync(ActorContext actor, UploadDocumentCommand command, UploadedContent content, CancellationToken cancellationToken = default);

    Task<CommandResult<UploadOutcome>> UploadVersionAsync(ActorContext actor, UploadVersionCommand command, UploadedContent content, CancellationToken cancellationToken = default);

    Task<CommandResult<Guid>> PlaceVersionAsync(ActorContext actor, PlaceVersionCommand command, CancellationToken cancellationToken = default);

    Task<CommandResult<Guid>> RecordDocumentEvidenceAsync(ActorContext actor, RecordDocumentEvidenceCommand command, CancellationToken cancellationToken = default);

    Task<CommandResult<Guid>> RetractEvidenceAsync(ActorContext actor, RetractEvidenceCommand command, CancellationToken cancellationToken = default);

    Task<CommandResult<Guid>> RemoveLinkAsync(ActorContext actor, RemoveLinkCommand command, CancellationToken cancellationToken = default);

    Task<CommandResult<Guid>> MoveLinkAsync(ActorContext actor, MoveLinkCommand command, CancellationToken cancellationToken = default);

    Task<CommandResult<Guid>> WithdrawVersionAsync(ActorContext actor, WithdrawVersionCommand command, CancellationToken cancellationToken = default);

    Task<CommandResult<Guid>> WithdrawDocumentAsync(ActorContext actor, WithdrawDocumentCommand command, CancellationToken cancellationToken = default);

    Task<CommandResult<Guid>> ReinstateVersionAsync(ActorContext actor, ReinstateVersionCommand command, CancellationToken cancellationToken = default);

    Task<CommandResult<Guid>> EditMetadataAsync(ActorContext actor, EditDocumentMetadataCommand command, CancellationToken cancellationToken = default);
}

/// <summary>
/// One version as a user may see it. Hashes, paths and volume codes are storage internals and never appear here
/// (DOCUMENT_MODEL.md §7.7, §19 principle 7).
/// </summary>
public sealed record DocumentVersionView(
    Guid Id,
    int VersionNo,
    string OriginalFileName,
    long ByteSize,
    string MimeType,
    FileFamily Family,
    bool IsMacroEnabled,
    DateOnly? DocumentDate,
    DateTimeOffset UploadedAt,
    UserRef UploadedBy,
    DocumentVersionStatus Status,
    int RowVersion,
    string? WithdrawalReasonCode,
    string? WithdrawalNote,
    DateTimeOffset? WithdrawnAt,
    UserRef? WithdrawnBy);

/// <summary>
/// One placement of a document in a context of the case being viewed, with only the versions that placement exposes
/// (§10.1): exactly its pinned version, or — for a floating home-case placement — the document's versions, newest first.
/// </summary>
/// <param name="IsHomeCase">The document entered the system in this case: new versions and document-level corrections happen here.</param>
public sealed record DocumentPlacementView(
    Guid LinkId,
    Guid DocumentId,
    string Title,
    string KindCode,
    string? Description,
    string? DocumentReference,
    DocumentStatus DocumentStatus,
    int DocumentRowVersion,
    DocumentTarget Target,
    string RoleCode,
    bool IsOrigin,
    Guid? PinnedVersionId,
    short? Ordinal,
    DateTimeOffset LinkedAt,
    UserRef LinkedBy,
    DocumentLinkStatus Status,
    DateTimeOffset? RemovedAt,
    UserRef? RemovedBy,
    string? RemovalReason,
    int RowVersion,
    bool IsHomeCase,
    IReadOnlyList<DocumentVersionView> Versions)
{
    public bool IsPinned => PinnedVersionId is not null;

    public bool IsActive => Status == DocumentLinkStatus.Active;

    /// <summary>What this placement shows as the file: its pinned version, or the document's ACTIVE version.</summary>
    public DocumentVersionView? Shown => PinnedVersionId is { } pinned
        ? Versions.FirstOrDefault(version => version.Id == pinned)
        : Versions.FirstOrDefault(version => version.Status == DocumentVersionStatus.Active);
}

/// <summary>Every placement whose context lies in one case — active ones and removed ones, which stay as history.</summary>
public sealed record CaseDocuments(Guid CaseId, IReadOnlyList<DocumentPlacementView> Placements)
{
    public static readonly CaseDocuments Empty = new(Guid.Empty, []);

    /// <summary>Placements in one context, the letter's own order first (DOCUMENT_MODEL.md §19 principle 3).</summary>
    public IReadOnlyList<DocumentPlacementView> For(DocumentTargetKind kind, Guid? id) =>
        id is null
            ? []
            : Placements
                .Where(placement => placement.Target.Kind == kind && placement.Target.Id == id)
                .OrderBy(placement => placement.Status)
                .ThenBy(placement => placement.RoleCode == DocumentLinkRoleCodes.PrimaryLetter ? 0 : 1)
                .ThenBy(placement => placement.Ordinal ?? short.MaxValue)
                .ThenBy(placement => placement.LinkedAt)
                .ToArray();

    public DocumentPlacementView? Find(Guid linkId) => Placements.FirstOrDefault(placement => placement.LinkId == linkId);
}

/// <summary>An exposed version the actor may place elsewhere in the case, as offered by the attach screens.</summary>
public sealed record AttachableVersion(Guid SourceLinkId, Guid DocumentId, string Title, DocumentTarget SourceTarget, string SourceRoleCode, DocumentVersionView Version);

/// <summary>
/// An authorized download: a readable stream of the exact stored bytes and the name to deliver them under. The
/// storage path and hash stay inside the application.
/// </summary>
public sealed class DocumentDownload(Stream content, string fileName, long byteSize) : IAsyncDisposable
{
    public Stream Content { get; } = content;

    public string FileName { get; } = fileName;

    public long ByteSize { get; } = byteSize;

    public ValueTask DisposeAsync() => Content.DisposeAsync();
}

public interface IDocumentQueries
{
    /// <summary>Every placement in the case's contexts, with the versions each exposes. Empty for an invisible case.</summary>
    Task<CommandResult<CaseDocuments>> GetCaseDocumentsAsync(ActorContext actor, Guid caseId, CancellationToken cancellationToken = default);

    /// <summary>Versions the actor can see in this case and could place elsewhere (withdrawn versions are not offered).</summary>
    Task<IReadOnlyList<AttachableVersion>> ListAttachableAsync(ActorContext actor, Guid caseId, CancellationToken cancellationToken = default);

    /// <summary>
    /// The per-request, per-version check of DOCUMENT_MODEL.md §10.1: the link must be ACTIVE, expose the version, and
    /// lie in a case the actor may view — evaluated now, on every download. A refused or unknown request is NotFound,
    /// so nothing is disclosed by guessing. A successful one is audited as DOWNLOAD with the context it went through.
    /// </summary>
    Task<CommandResult<DocumentDownload>> OpenDownloadAsync(ActorContext actor, Guid caseId, Guid linkId, Guid versionId, CancellationToken cancellationToken = default);
}

/// <summary>An integrity finding (DOCUMENT_MODEL.md §11.1). Nothing is repaired, moved or deleted because of one.</summary>
public enum IntegrityFindingKind
{
    /// <summary>I1 — a version row points at a missing object. CRITICAL.</summary>
    MissingObject = 1,

    /// <summary>I2 — an object no version row references. Informational: reported and retained.</summary>
    UnreferencedObject,

    /// <summary>I3 — the stored size differs from <c>byte_size</c>. CRITICAL.</summary>
    SizeMismatch,

    /// <summary>I4 — the re-computed SHA-256 differs from <c>content_hash</c>. CRITICAL.</summary>
    HashMismatch,

    /// <summary>I5 — the object could not be read. WARNING, escalating if it persists.</summary>
    Unreadable,
}

/// <param name="VersionId">Null for an unreferenced object.</param>
/// <param name="StoragePath">The relative storage path: for administrators, never shown to business users.</param>
public sealed record IntegrityFinding(IntegrityFindingKind Kind, Guid? VersionId, string StoragePath, string Detail);

public sealed record IntegrityReport(int VersionsChecked, int ObjectsScanned, IReadOnlyList<IntegrityFinding> Findings)
{
    public bool IsClean => Findings.All(finding => finding.Kind == IntegrityFindingKind.UnreferencedObject);
}

public interface IDocumentIntegrityService
{
    /// <summary>
    /// Checks every version's object (existence and size; with <paramref name="rehash"/> also SHA-256), and optionally
    /// scans the store for unreferenced objects. Findings are audited as JOB events; clean full checks stamp
    /// <c>integrity_checked_at</c>. Read-only against the bytes.
    /// </summary>
    Task<IntegrityReport> CheckAsync(bool rehash, bool scanForUnreferenced, CancellationToken cancellationToken = default);
}
