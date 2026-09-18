using Npgsql;
using Rcs.Application.Cases;
using Rcs.Domain.Vocabulary;
using Rcs.Infrastructure.Persistence;

namespace Rcs.Infrastructure.Cases;

internal sealed record LetterRow(
    Guid Id,
    Guid CaseId,
    CorrespondenceDirection Direction,
    string KindCode,
    string? LetterNumber,
    string? RegistryNumber,
    DateOnly? LetterDate,
    DateTimeOffset? SentAt,
    DateTimeOffset? ReceivedAt,
    string? Subject,
    OrganizationRef Sender,
    OrganizationRef Recipient,
    DateTimeOffset RegisteredAt,
    UserRef RegisteredBy)
{
    public LetterView ToView() => new(Id, Direction, LetterNumber, RegistryNumber, LetterDate, SentAt, ReceivedAt, Subject, Sender, Recipient, RegisteredAt, RegisteredBy);
}

internal sealed record RequestRowFull(
    Guid Id,
    Guid CaseId,
    string RequestNumber,
    OrganizationRef Target,
    string Subject,
    string? RequestedItemsNote,
    RequestStatus Status,
    int RowVersion,
    Guid? DispatchCorrespondenceId,
    DateTimeOffset? SentAt,
    DateTimeOffset? DueAt,
    Guid? SourceRequirementId,
    DateTimeOffset? ClosedAt);

internal sealed record ResponseRowFull(
    Guid Id,
    Guid RequestId,
    Guid CorrespondenceId,
    Guid CorrespondenceCaseId,
    string TypeCode,
    string OutcomeCode,
    bool IsConclusive,
    string? Summary,
    ResponseStatus Status,
    DateOnly? ResponseDate,
    DateTimeOffset? ReceivedAt,
    DateTimeOffset RecordedAt,
    UserRef RecordedBy);

internal sealed record RequirementRowFull(
    Guid Id,
    Guid CaseId,
    Guid? SourceResponseId,
    string Title,
    string? Description,
    bool IsBlocking,
    RequirementStatus Status,
    int RowVersion,
    DateTimeOffset RaisedAt,
    DateTimeOffset? DueAt,
    OrganizationRef? RaisedBy,
    OrganizationRef? AddressedTo,
    DateTimeOffset? StartedAt,
    DateTimeOffset? ResolvedAt,
    UserRef? ResolvedBy,
    string? ResolutionNote,
    string? WaiverReasonCode,
    string? VoidReasonCode,
    Guid? VoidSourceResponseId,
    string? FailureReasonNote,
    Guid CreatedByUserId);

internal sealed record EvidenceRowFull(Guid Id, Guid RequirementId, Guid? ResponseId, string? Note, DateTimeOffset RecordedAt, UserRef RecordedBy);

/// <summary>Every row of one or more cases that the list, the workspace and derived progress need.</summary>
internal sealed record CaseGraph(
    IReadOnlyList<LetterRow> Letters,
    IReadOnlyList<RequestRowFull> Requests,
    IReadOnlyList<ResponseRowFull> Responses,
    IReadOnlyList<RequirementRowFull> Requirements,
    IReadOnlyList<EvidenceRowFull> Evidence);

/// <summary>
/// Loads the workflow spine of a set of cases with a small number of focused queries (ARCHITECTURE.md §7.4: complex
/// reads are hand-written SQL). Nothing here is cached and nothing is stored: progress is derived from these rows.
/// </summary>
internal static class CaseGraphLoader
{
    /// <param name="transaction">
    /// The caller's transaction when the read happens inside one — a command evaluating closure guards under the
    /// case lock reads the same rows as the workspace does, so there is one loader and no second interpretation.
    /// </param>
    public static async Task<CaseGraph> LoadAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        IReadOnlyCollection<Guid> caseIds,
        bool includeLetters,
        CancellationToken cancellationToken)
    {
        if (caseIds.Count == 0)
        {
            return new CaseGraph([], [], [], [], []);
        }

        var letters = includeLetters
            ? await new NpgsqlCommand("""
                SELECT c.id, c.case_id, c.direction, k.code AS kind_code, c.letter_number, c.registry_number, c.letter_date,
                       c.sent_at, c.received_at, c.subject, c.registered_at,
                       s.id AS sender_id, s.official_name AS sender_official, s.short_name AS sender_short,
                       r.id AS recipient_id, r.official_name AS recipient_official, r.short_name AS recipient_short,
                       u.id AS registered_by_id, u.display_name AS registered_by_name
                FROM rcs.correspondence AS c
                JOIN rcs.correspondence_kind AS k ON k.id = c.correspondence_kind_id
                JOIN rcs.organization AS s ON s.id = c.sender_organization_id
                JOIN rcs.organization AS r ON r.id = c.recipient_organization_id
                JOIN rcs.app_user AS u ON u.id = c.registered_by_user_id
                WHERE c.case_id = ANY(@cases)
                """, connection, transaction).WithIds("cases", caseIds).ListAsync(reader => new LetterRow(
                    reader.Uuid("id"),
                    reader.Uuid("case_id"),
                    VocabularyCodes.FromCode<CorrespondenceDirection>(reader.Text("direction")),
                    reader.Text("kind_code"),
                    reader.TextOrNull("letter_number"),
                    reader.TextOrNull("registry_number"),
                    reader.DateOrNull("letter_date"),
                    reader.InstantOrNull("sent_at"),
                    reader.InstantOrNull("received_at"),
                    reader.TextOrNull("subject"),
                    Organization(reader, "sender"),
                    Organization(reader, "recipient"),
                    reader.Instant("registered_at"),
                    new UserRef(reader.Uuid("registered_by_id"), reader.Text("registered_by_name"))), cancellationToken)
            : [];

        var requests = await new NpgsqlCommand("""
            SELECT r.id, r.case_id, r.request_number, r.subject, r.requested_items_note, r.status, r.row_version,
                   r.dispatch_correspondence_id, r.due_at, r.source_requirement_id, r.closed_at, d.sent_at,
                   t.id AS target_id, t.official_name AS target_official, t.short_name AS target_short
            FROM rcs.request AS r
            JOIN rcs.organization AS t ON t.id = r.target_organization_id
            LEFT JOIN rcs.correspondence AS d ON d.id = r.dispatch_correspondence_id
            WHERE r.case_id = ANY(@cases)
            """, connection, transaction).WithIds("cases", caseIds).ListAsync(reader => new RequestRowFull(
                reader.Uuid("id"),
                reader.Uuid("case_id"),
                reader.Text("request_number"),
                Organization(reader, "target"),
                reader.Text("subject"),
                reader.TextOrNull("requested_items_note"),
                VocabularyCodes.FromCode<RequestStatus>(reader.Text("status")),
                reader.Int("row_version"),
                reader.UuidOrNull("dispatch_correspondence_id"),
                reader.InstantOrNull("sent_at"),
                reader.InstantOrNull("due_at"),
                reader.UuidOrNull("source_requirement_id"),
                reader.InstantOrNull("closed_at")), cancellationToken);

        var responses = await new NpgsqlCommand("""
            SELECT p.id, p.request_id, p.correspondence_id, c.case_id AS correspondence_case_id, ty.code AS type_code, oc.code AS outcome_code,
                   p.is_conclusive, p.summary, p.status, p.response_date, p.received_at, p.recorded_at,
                   u.id AS recorded_by_id, u.display_name AS recorded_by_name
            FROM rcs.response AS p
            JOIN rcs.request AS r ON r.id = p.request_id
            JOIN rcs.correspondence AS c ON c.id = p.correspondence_id
            JOIN rcs.response_type AS ty ON ty.id = p.response_type_id
            JOIN rcs.response_outcome AS oc ON oc.id = p.response_outcome_id
            JOIN rcs.app_user AS u ON u.id = p.recorded_by_user_id
            WHERE r.case_id = ANY(@cases)
            """, connection, transaction).WithIds("cases", caseIds).ListAsync(reader => new ResponseRowFull(
                reader.Uuid("id"),
                reader.Uuid("request_id"),
                reader.Uuid("correspondence_id"),
                reader.Uuid("correspondence_case_id"),
                reader.Text("type_code"),
                reader.Text("outcome_code"),
                reader.Bool("is_conclusive"),
                reader.TextOrNull("summary"),
                VocabularyCodes.FromCode<ResponseStatus>(reader.Text("status")),
                reader.DateOrNull("response_date"),
                reader.InstantOrNull("received_at"),
                reader.Instant("recorded_at"),
                new UserRef(reader.Uuid("recorded_by_id"), reader.Text("recorded_by_name"))), cancellationToken);

        var requirements = await new NpgsqlCommand("""
            SELECT q.id, q.case_id, q.source_response_id, q.title, q.description, q.is_blocking, q.status, q.row_version,
                   q.raised_at, q.due_at, q.started_at, q.resolved_at, q.resolution_note, q.void_source_response_id,
                   q.failure_reason_note, q.created_by_user_id,
                   rb.id AS raised_by_id, rb.official_name AS raised_by_official, rb.short_name AS raised_by_short,
                   ad.id AS addressed_to_id, ad.official_name AS addressed_to_official, ad.short_name AS addressed_to_short,
                   ru.id AS resolved_by_id, ru.display_name AS resolved_by_name,
                   wr.code AS waiver_reason_code, vr.code AS void_reason_code
            FROM rcs.requirement AS q
            LEFT JOIN rcs.organization AS rb ON rb.id = q.raised_by_organization_id
            LEFT JOIN rcs.organization AS ad ON ad.id = q.addressed_to_organization_id
            LEFT JOIN rcs.app_user AS ru ON ru.id = q.resolved_by_user_id
            LEFT JOIN rcs.waiver_reason AS wr ON wr.id = q.waiver_reason_id
            LEFT JOIN rcs.void_reason AS vr ON vr.id = q.void_reason_id
            WHERE q.case_id = ANY(@cases)
            """, connection, transaction).WithIds("cases", caseIds).ListAsync(reader => new RequirementRowFull(
                reader.Uuid("id"),
                reader.Uuid("case_id"),
                reader.UuidOrNull("source_response_id"),
                reader.Text("title"),
                reader.TextOrNull("description"),
                reader.Bool("is_blocking"),
                VocabularyCodes.FromCode<RequirementStatus>(reader.Text("status")),
                reader.Int("row_version"),
                reader.Instant("raised_at"),
                reader.InstantOrNull("due_at"),
                OrganizationOrNull(reader, "raised_by"),
                OrganizationOrNull(reader, "addressed_to"),
                reader.InstantOrNull("started_at"),
                reader.InstantOrNull("resolved_at"),
                reader.UuidOrNull("resolved_by_id") is { } resolvedById ? new UserRef(resolvedById, reader.Text("resolved_by_name")) : null,
                reader.TextOrNull("resolution_note"),
                reader.TextOrNull("waiver_reason_code"),
                reader.TextOrNull("void_reason_code"),
                reader.UuidOrNull("void_source_response_id"),
                reader.TextOrNull("failure_reason_note"),
                reader.Uuid("created_by_user_id")), cancellationToken);

        var evidence = await new NpgsqlCommand("""
            SELECT e.id, e.requirement_id, e.response_id, e.note, e.recorded_at,
                   u.id AS recorded_by_id, u.display_name AS recorded_by_name
            FROM rcs.requirement_evidence AS e
            JOIN rcs.requirement AS q ON q.id = e.requirement_id
            JOIN rcs.app_user AS u ON u.id = e.recorded_by_user_id
            WHERE q.case_id = ANY(@cases) AND e.status = 'ACTIVE'
            """, connection, transaction).WithIds("cases", caseIds).ListAsync(reader => new EvidenceRowFull(
                reader.Uuid("id"),
                reader.Uuid("requirement_id"),
                reader.UuidOrNull("response_id"),
                reader.TextOrNull("note"),
                reader.Instant("recorded_at"),
                new UserRef(reader.Uuid("recorded_by_id"), reader.Text("recorded_by_name"))), cancellationToken);

        return new CaseGraph(letters, requests, responses, requirements, evidence);
    }

    public static OrganizationRef Organization(NpgsqlDataReader reader, string prefix) =>
        new(reader.Uuid($"{prefix}_id"), reader.TextOrNull($"{prefix}_short") ?? reader.Text($"{prefix}_official"), reader.Text($"{prefix}_official"));

    private static OrganizationRef? OrganizationOrNull(NpgsqlDataReader reader, string prefix) =>
        reader.UuidOrNull($"{prefix}_id") is null ? null : Organization(reader, prefix);
}
