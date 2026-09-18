using Npgsql;
using Rcs.Application.Documents;
using Rcs.Domain.Documents;
using Rcs.Domain.Vocabulary;
using Rcs.Infrastructure.Persistence;

namespace Rcs.Infrastructure.Documents;

internal sealed record LinkRow(
    Guid Id,
    Guid DocumentId,
    Guid? VersionId,
    DocumentTarget Target,
    string RoleCode,
    bool IsOrigin,
    DocumentLinkStatus Status,
    Guid ContextCaseId,
    Guid LinkedByUserId,
    int RowVersion);

internal sealed record VersionRow(
    Guid Id,
    Guid DocumentId,
    int VersionNo,
    string OriginalFileName,
    string ContentHash,
    string VolumeCode,
    string RelativePath,
    long ByteSize,
    string MimeType,
    DocumentVersionStatus Status,
    Guid UploadedByUserId,
    int RowVersion);

internal sealed record DocumentRow(Guid Id, string Title, string KindCode, string? Description, string? DocumentReference, DocumentStatus Status, Guid CreatedByUserId, int RowVersion);

/// <summary>The resolved target of a placement: the case it lies in and the state the placement rules read.</summary>
internal sealed record TargetRow(Guid CaseId, RequirementStatus? RequirementStatus, FinalResultStatus? FinalResultStatus);

/// <summary>
/// Reads shared by every document command and query. The case of a placement always comes from
/// <c>rcs.document_link_context</c> — the one definition of "which case is this link in" (DOCUMENT_MODEL.md §10.1).
/// </summary>
internal static class DocumentSql
{
    private const string LinkColumns = """
        l.id, l.document_id, l.document_version_id, l.correspondence_id, l.requirement_id, l.request_id, l.response_id,
        l.final_result_id, l.case_id, r.code AS role_code, l.is_origin, l.status, l.linked_by_user_id, l.row_version,
        ctx.context_case_id
        """;

    private const string LinkFrom = """
        FROM rcs.document_link AS l
        JOIN rcs.document_link_role AS r ON r.id = l.document_link_role_id
        JOIN rcs.document_link_context AS ctx ON ctx.document_link_id = l.id
        """;

    public static Task<LinkRow?> LoadLinkAsync(PostgresUnitOfWork unitOfWork, Guid linkId, CancellationToken cancellationToken) =>
        unitOfWork.Command($"SELECT {LinkColumns} {LinkFrom} WHERE l.id = @id")
            .With("id", linkId)
            .SingleOrDefaultAsync(MapLink, cancellationToken);

    public static Task<List<LinkRow>> ActiveLinksOfDocumentAsync(PostgresUnitOfWork unitOfWork, Guid documentId, CancellationToken cancellationToken) =>
        unitOfWork.Command($"SELECT {LinkColumns} {LinkFrom} WHERE l.document_id = @id AND l.status = 'ACTIVE'")
            .With("id", documentId)
            .ListAsync(MapLink, cancellationToken);

    public static LinkRow MapLink(NpgsqlDataReader reader) => new(
        reader.Uuid("id"),
        reader.Uuid("document_id"),
        reader.UuidOrNull("document_version_id"),
        TargetOf(reader),
        reader.Text("role_code"),
        reader.Bool("is_origin"),
        VocabularyCodes.FromCode<DocumentLinkStatus>(reader.Text("status")),
        reader.Uuid("context_case_id"),
        reader.Uuid("linked_by_user_id"),
        reader.Int("row_version"));

    public static DocumentTarget TargetOf(NpgsqlDataReader reader) =>
        reader.UuidOrNull("correspondence_id") is { } correspondence ? new(DocumentTargetKind.Correspondence, correspondence)
        : reader.UuidOrNull("requirement_id") is { } requirement ? new(DocumentTargetKind.Requirement, requirement)
        : reader.UuidOrNull("request_id") is { } request ? new(DocumentTargetKind.Request, request)
        : reader.UuidOrNull("response_id") is { } response ? new(DocumentTargetKind.Response, response)
        : reader.UuidOrNull("final_result_id") is { } result ? new(DocumentTargetKind.FinalResult, result)
        : new(DocumentTargetKind.Case, reader.Uuid("case_id"));

    /// <summary>§10.1 exposes(L, v): an ACTIVE link of the same document, pinned to exactly v or floating.</summary>
    public static bool Exposes(LinkRow link, VersionRow version) =>
        link.Status == DocumentLinkStatus.Active
        && link.DocumentId == version.DocumentId
        && (link.VersionId is null || link.VersionId == version.Id);

    public static Task<VersionRow?> LoadVersionAsync(PostgresUnitOfWork unitOfWork, Guid versionId, CancellationToken cancellationToken) =>
        unitOfWork.Command("""
                SELECT id, document_id, version_no, original_filename, content_hash, storage_volume_code, stored_relative_path,
                       byte_size, mime_type, status, uploaded_by_user_id, row_version
                FROM rcs.document_version WHERE id = @id
                """)
            .With("id", versionId)
            .SingleOrDefaultAsync(MapVersion, cancellationToken);

    public static Task<List<VersionRow>> VersionsOfDocumentAsync(PostgresUnitOfWork unitOfWork, Guid documentId, CancellationToken cancellationToken) =>
        unitOfWork.Command("""
                SELECT id, document_id, version_no, original_filename, content_hash, storage_volume_code, stored_relative_path,
                       byte_size, mime_type, status, uploaded_by_user_id, row_version
                FROM rcs.document_version WHERE document_id = @id ORDER BY version_no
                """)
            .With("id", documentId)
            .ListAsync(MapVersion, cancellationToken);

    private static VersionRow MapVersion(NpgsqlDataReader reader) => new(
        reader.Uuid("id"),
        reader.Uuid("document_id"),
        reader.Int("version_no"),
        reader.Text("original_filename"),
        reader.Text("content_hash"),
        reader.Text("storage_volume_code"),
        reader.Text("stored_relative_path"),
        reader.Long("byte_size"),
        reader.Text("mime_type"),
        VocabularyCodes.FromCode<DocumentVersionStatus>(reader.Text("status")),
        reader.Uuid("uploaded_by_user_id"),
        reader.Int("row_version"));

    /// <summary>
    /// Loads the document, locking its row when <paramref name="forUpdate"/>: upload of a version, withdrawal of the
    /// ACTIVE version and reinstatement are serialised per document (ADR-027); the partial unique index is the backstop.
    /// </summary>
    public static Task<DocumentRow?> LoadDocumentAsync(PostgresUnitOfWork unitOfWork, Guid documentId, bool forUpdate, CancellationToken cancellationToken) =>
        unitOfWork.Command($"""
                SELECT d.id, d.title, k.code AS kind_code, d.description, d.document_reference, d.status, d.created_by_user_id, d.row_version
                FROM rcs.document AS d JOIN rcs.document_kind AS k ON k.id = d.document_kind_id
                WHERE d.id = @id{(forUpdate ? " FOR UPDATE OF d" : string.Empty)}
                """)
            .With("id", documentId)
            .SingleOrDefaultAsync(reader => new DocumentRow(
                reader.Uuid("id"),
                reader.Text("title"),
                reader.Text("kind_code"),
                reader.TextOrNull("description"),
                reader.TextOrNull("document_reference"),
                VocabularyCodes.FromCode<DocumentStatus>(reader.Text("status")),
                reader.Uuid("created_by_user_id"),
                reader.Int("row_version")), cancellationToken);

    /// <summary>
    /// The document's home case: the case its ACTIVE origin placement lies in — where the file entered the system
    /// (DOCUMENT_MODEL.md §4.4). Null for a withdrawn document with no active placement.
    /// </summary>
    public static Task<Guid?> HomeCaseAsync(PostgresUnitOfWork unitOfWork, Guid documentId, CancellationToken cancellationToken) =>
        unitOfWork.Command("""
                SELECT ctx.context_case_id
                FROM rcs.document_link AS l JOIN rcs.document_link_context AS ctx ON ctx.document_link_id = l.id
                WHERE l.document_id = @id AND l.status = 'ACTIVE' AND l.is_origin
                """)
            .With("id", documentId)
            .ScalarAsync<Guid?>(cancellationToken);

    /// <summary>The case a target lies in, and the state the placement rules read; null when it does not exist (or is void).</summary>
    public static Task<TargetRow?> ResolveTargetAsync(PostgresUnitOfWork unitOfWork, DocumentTarget target, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        var sql = target.Kind switch
        {
            DocumentTargetKind.Correspondence => "SELECT case_id, NULL::text AS requirement_status, NULL::text AS result_status FROM rcs.correspondence WHERE id = @id AND status <> 'VOID'",
            DocumentTargetKind.Requirement => "SELECT case_id, status AS requirement_status, NULL::text AS result_status FROM rcs.requirement WHERE id = @id",
            DocumentTargetKind.Request => "SELECT case_id, NULL::text AS requirement_status, NULL::text AS result_status FROM rcs.request WHERE id = @id AND status <> 'VOID'",
            DocumentTargetKind.Response => """
                SELECT r.case_id, NULL::text AS requirement_status, NULL::text AS result_status
                FROM rcs.response AS p JOIN rcs.request AS r ON r.id = p.request_id WHERE p.id = @id AND p.status <> 'VOID'
                """,
            DocumentTargetKind.FinalResult => "SELECT case_id, NULL::text AS requirement_status, status AS result_status FROM rcs.final_result WHERE id = @id",
            DocumentTargetKind.Case => "SELECT id AS case_id, NULL::text AS requirement_status, NULL::text AS result_status FROM rcs.case_record WHERE id = @id",
            _ => throw new ArgumentOutOfRangeException(nameof(target), target.Kind, "Unknown target."),
        };

        return unitOfWork.Command(sql).With("id", target.Id).SingleOrDefaultAsync(
            reader => new TargetRow(
                reader.Uuid("case_id"),
                reader.TextOrNull("requirement_status") is { } requirement ? VocabularyCodes.FromCode<RequirementStatus>(requirement) : null,
                reader.TextOrNull("result_status") is { } result ? VocabularyCodes.FromCode<FinalResultStatus>(result) : null),
            cancellationToken);
    }

    public static string TargetColumn(DocumentTargetKind kind) => kind switch
    {
        DocumentTargetKind.Correspondence => "correspondence_id",
        DocumentTargetKind.Requirement => "requirement_id",
        DocumentTargetKind.Request => "request_id",
        DocumentTargetKind.Response => "response_id",
        DocumentTargetKind.FinalResult => "final_result_id",
        DocumentTargetKind.Case => "case_id",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown target."),
    };

    /// <summary>The <c>audit_event.entity_type</c> name of a target kind.</summary>
    public static string TargetEntityType(DocumentTargetKind kind) => kind switch
    {
        DocumentTargetKind.Correspondence => AuditEntityTypes.Correspondence,
        DocumentTargetKind.Requirement => AuditEntityTypes.Requirement,
        DocumentTargetKind.Request => AuditEntityTypes.Request,
        DocumentTargetKind.Response => AuditEntityTypes.Response,
        DocumentTargetKind.FinalResult => AuditEntityTypes.FinalResult,
        _ => AuditEntityTypes.Case,
    };

    public static Task<Guid?> LookupIdAsync(PostgresUnitOfWork unitOfWork, string table, string? code, CancellationToken cancellationToken) =>
        unitOfWork.Command($"SELECT id FROM rcs.{table} WHERE code = @code AND is_active")
            .With("code", code ?? string.Empty)
            .ScalarAsync<Guid?>(cancellationToken);

    /// <summary>
    /// "Depends on it" (PERMISSIONS.md §24.1, DOCUMENT_MODEL.md §8.1) for a document or one of its versions: cited as
    /// ACTIVE evidence, pinned by a decision that was issued, or placed on a letter something was registered against —
    /// an incoming letter a response was recorded from, or an outgoing letter whose request has been answered.
    /// </summary>
    public static async Task<bool> HasDependentsAsync(PostgresUnitOfWork unitOfWork, Guid documentId, Guid? versionId, CancellationToken cancellationToken) =>
        await unitOfWork.Command("""
                SELECT EXISTS (
                           SELECT 1 FROM rcs.requirement_evidence AS e
                           WHERE e.document_id = @document AND e.status = 'ACTIVE'
                             AND (@version::uuid IS NULL OR e.document_version_id = @version))
                    OR EXISTS (
                           SELECT 1 FROM rcs.document_link AS l JOIN rcs.final_result AS f ON f.id = l.final_result_id
                           WHERE l.document_id = @document AND l.status = 'ACTIVE' AND f.status IN ('ISSUED', 'SUPERSEDED', 'REVOKED')
                             AND (@version::uuid IS NULL OR l.document_version_id = @version))
                    OR EXISTS (
                           SELECT 1 FROM rcs.document_link AS l
                           WHERE l.document_id = @document AND l.status = 'ACTIVE' AND l.correspondence_id IS NOT NULL
                             AND (@version::uuid IS NULL OR l.document_version_id = @version)
                             AND (EXISTS (SELECT 1 FROM rcs.response AS p WHERE p.correspondence_id = l.correspondence_id AND p.status <> 'VOID')
                                  OR EXISTS (SELECT 1 FROM rcs.request AS rq JOIN rcs.response AS p ON p.request_id = rq.id
                                             WHERE rq.dispatch_correspondence_id = l.correspondence_id AND p.status <> 'VOID')))
                """)
            .With("document", documentId)
            .With("version", versionId)
            .ScalarAsync<bool>(cancellationToken);

    /// <summary>The same test for one placement: a letter placement once the letter has something registered against it.</summary>
    public static async Task<bool> LinkHasDependentsAsync(PostgresUnitOfWork unitOfWork, LinkRow link, CancellationToken cancellationToken) =>
        link.Target.Kind == DocumentTargetKind.Correspondence
        && await unitOfWork.Command("""
                SELECT EXISTS (SELECT 1 FROM rcs.response AS p WHERE p.correspondence_id = @letter AND p.status <> 'VOID')
                    OR EXISTS (SELECT 1 FROM rcs.request AS rq JOIN rcs.response AS p ON p.request_id = rq.id
                               WHERE rq.dispatch_correspondence_id = @letter AND p.status <> 'VOID')
                """)
            .With("letter", link.Target.Id)
            .ScalarAsync<bool>(cancellationToken);

    /// <summary>A version pinned by an ACTIVE placement on a decision that was issued — immune to correction (§8.4).</summary>
    public static async Task<bool> PinnedByIssuedDecisionAsync(PostgresUnitOfWork unitOfWork, Guid versionId, CancellationToken cancellationToken) =>
        await unitOfWork.Command("""
                SELECT EXISTS (
                    SELECT 1 FROM rcs.document_link AS l JOIN rcs.final_result AS f ON f.id = l.final_result_id
                    WHERE l.document_version_id = @version AND l.status = 'ACTIVE' AND f.status IN ('ISSUED', 'SUPERSEDED', 'REVOKED'))
                """)
            .With("version", versionId)
            .ScalarAsync<bool>(cancellationToken);
}
