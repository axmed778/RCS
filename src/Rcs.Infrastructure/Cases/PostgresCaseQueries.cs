using System.Text.Json;
using Npgsql;
using Rcs.Application.Cases;
using Rcs.Application.Common;
using Rcs.Domain.Authorization;
using Rcs.Domain.Progress;
using Rcs.Domain.Vocabulary;
using Rcs.Application.Identity;
using Rcs.Infrastructure.Identity;
using Rcs.Infrastructure.Persistence;

namespace Rcs.Infrastructure.Cases;

/// <summary>
/// The read side of cases. Visibility is filtered in the query, never after retrieval, and restricted cases the actor
/// cannot see are absent from lists and counts (PERMISSIONS.md §19.2, §27.2). Derived progress is computed here from
/// the loaded rows and is never stored (ADR-029).
/// </summary>
internal sealed class PostgresCaseQueries(NpgsqlDataSource dataSource, TimeProvider timeProvider, ProgressEvaluator progress) : ICaseQueries
{
    private const string VisibilityPredicate = """
        (NOT c.is_restricted OR @is_chief OR EXISTS (
            SELECT 1 FROM rcs.assignment AS va
            WHERE va.status = 'ACTIVE' AND va.assignee_user_id = @actor
              AND va.valid_from <= @now AND (va.valid_until IS NULL OR va.valid_until > @now)
              AND (va.case_id = c.id
                   OR va.request_id IN (SELECT id FROM rcs.request WHERE case_id = c.id)
                   OR va.requirement_id IN (SELECT id FROM rcs.requirement WHERE case_id = c.id))))
        """;

    private static readonly JsonDocumentOptions JsonOptions = new();

    public async Task<IReadOnlyList<CaseListItem>> ListAsync(ActorContext actor, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        var authority = await AuthorityAsync(connection, actor, cancellationToken);
        if (authority is null || !authority.HasBusinessRole)
        {
            return [];
        }

        var now = timeProvider.GetUtcNow();
        var headers = await HeadersAsync(connection, authority, now, predicate: null, cancellationToken);
        if (headers.Count == 0)
        {
            return [];
        }

        var graph = await CaseGraphLoader.LoadAsync(connection, headers.Select(header => header.Id).ToArray(), includeLetters: false, cancellationToken);
        return headers
            .Select(header => new CaseListItem(
                header.Id,
                header.CaseNumber,
                header.Title,
                header.RequestingOrganization,
                header.Responsible,
                header.State,
                header.RegisteredAt,
                header.IncomingLetterNumber,
                header.IncomingLetterDate,
                progress.Evaluate(SnapshotFor(header, graph), now)))
            .ToArray();
    }

    public async Task<DashboardSummary> GetDashboardAsync(ActorContext actor, CancellationToken cancellationToken = default)
    {
        var cases = await ListAsync(actor, cancellationToken);
        var open = cases.Where(item => item.State is CaseLifecycleState.Registered or CaseLifecycleState.Active or CaseLifecycleState.OnHold).ToArray();

        return new DashboardSummary(
            OpenCases: open.Length,
            WaitingForExternalResponse: open.Count(item => item.Progress.Requests.Values.Any(request =>
                request.State is RequestProgressState.AwaitingResponse or RequestProgressState.PartiallyAnswered or RequestProgressState.Overdue)),
            OpenRequirements: open.Sum(item => item.Progress.Summary.OpenRequirements),
            OverdueItems: open.Sum(item => item.Progress.Summary.Overdue),
            ReadyForFinalResult: open.Count(item => item.Progress.IsReadyForFinalResult),
            RecentCases: open.Take(8).ToArray());
    }

    public async Task<CommandResult<CaseWorkspace>> GetWorkspaceAsync(ActorContext actor, Guid caseId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        var authority = await AuthorityAsync(connection, actor, cancellationToken);
        if (authority is null || !authority.HasBusinessRole)
        {
            return CommandResult<CaseWorkspace>.Failure(CommandErrorKind.Forbidden, "auth.no_business_role");
        }

        var now = timeProvider.GetUtcNow();
        var headers = await HeadersAsync(connection, authority, now, "c.id = @case", cancellationToken, command => command.With("case", caseId));
        if (headers.Count == 0)
        {
            return CommandResult<CaseWorkspace>.Failure(CommandErrorKind.NotFound, "notfound.case");
        }

        var header = headers[0];
        var graph = await CaseGraphLoader.LoadAsync(connection, [caseId], includeLetters: true, cancellationToken);
        var letters = graph.Letters.ToDictionary(letter => letter.Id);

        var responsesByRequest = graph.Responses.ToLookup(response => response.RequestId);
        var requirementsByResponse = graph.Requirements.ToLookup(requirement => requirement.SourceResponseId);
        var childRequests = graph.Requests.ToLookup(request => request.SourceRequirementId);
        var evidenceByRequirement = graph.Evidence.ToLookup(evidence => evidence.RequirementId);
        var allResponses = new List<ResponseNode>();

        RequestNode BuildRequest(RequestRowFull request) => new(
            request.Id,
            request.RequestNumber,
            request.Target,
            request.Subject,
            request.RequestedItemsNote,
            request.Status,
            request.RowVersion,
            request.DispatchCorrespondenceId is { } dispatchId && letters.TryGetValue(dispatchId, out var dispatch) ? dispatch.ToView() : null,
            request.DueAt,
            request.SourceRequirementId,
            request.ClosedAt,
            responsesByRequest[request.Id].OrderBy(response => response.ReceivedAt ?? response.RecordedAt).Select(BuildResponse).ToArray());

        ResponseNode BuildResponse(ResponseRowFull response)
        {
            // A letter filed in another case stays governed by that case: the response shows its own facts only (A-6).
            var letter = response.CorrespondenceCaseId == caseId && letters.TryGetValue(response.CorrespondenceId, out var row) ? row.ToView() : null;
            var node = new ResponseNode(
                response.Id,
                response.RequestId,
                letter,
                response.TypeCode,
                response.OutcomeCode,
                response.IsConclusive,
                response.Summary,
                response.Status,
                response.ResponseDate,
                response.ReceivedAt,
                response.RecordedAt,
                response.RecordedBy,
                requirementsByResponse[response.Id].OrderBy(requirement => requirement.RaisedAt).Select(BuildRequirement).ToArray());
            allResponses.Add(node);
            return node;
        }

        RequirementNode BuildRequirement(RequirementRowFull requirement) => new(
            requirement.Id,
            requirement.SourceResponseId,
            requirement.SourceResponseId is { } sourceId ? graph.Responses.FirstOrDefault(response => response.Id == sourceId)?.RequestId : null,
            requirement.Title,
            requirement.Description,
            requirement.IsBlocking,
            requirement.Status,
            requirement.RowVersion,
            requirement.RaisedAt,
            requirement.DueAt,
            requirement.RaisedBy,
            requirement.AddressedTo,
            requirement.StartedAt,
            requirement.ResolvedAt,
            requirement.ResolvedBy,
            requirement.ResolutionNote,
            requirement.WaiverReasonCode,
            requirement.VoidReasonCode,
            requirement.VoidSourceResponseId,
            requirement.FailureReasonNote,
            requirement.CreatedByUserId,
            evidenceByRequirement[requirement.Id]
                .Select(evidence => new EvidenceView(evidence.Id, evidence.ResponseId, evidence.Note, evidence.RecordedAt, evidence.RecordedBy))
                .ToArray(),
            childRequests[requirement.Id].OrderBy(request => request.SentAt).Select(BuildRequest).ToArray());

        var topLevel = graph.Requests
            .Where(request => request.SourceRequirementId is null)
            .OrderBy(request => request.SentAt)
            .ThenBy(request => request.RequestNumber, StringComparer.Ordinal)
            .Select(BuildRequest)
            .ToArray();

        var initiating = graph.Letters.FirstOrDefault(letter => letter.KindCode == CorrespondenceKindCodes.Initiating);
        var activity = await ActivityAsync(connection, caseId, cancellationToken);
        var assigned = await IsAssignedAsync(connection, caseId, actor.UserId, now, cancellationToken);

        var workspace = new CaseWorkspace(
            new CaseHeader(header.Id, header.CaseNumber, header.Title, header.Subject, header.RequestingOrganization, header.Responsible,
                header.State, header.RegisteredAt, header.CreatedAt, header.CreatedBy, header.Notes, header.IsRestricted),
            initiating?.ToView(),
            topLevel,
            allResponses,
            progress.Evaluate(SnapshotFor(header, graph), now),
            activity,
            authority,
            assigned);

        return CommandResult<CaseWorkspace>.Success(workspace);
    }

    // ------------------------------------------------------------------ pieces

    private sealed record CaseHeaderRow(
        Guid Id,
        string CaseNumber,
        string Title,
        string? Subject,
        OrganizationRef RequestingOrganization,
        UserRef? Responsible,
        CaseLifecycleState State,
        DateTimeOffset RegisteredAt,
        DateTimeOffset CreatedAt,
        UserRef CreatedBy,
        string? Notes,
        bool IsRestricted,
        DateTimeOffset? ClosedAt,
        DateOnly? HoldUntil,
        string? HoldReason,
        string? IncomingLetterNumber,
        DateOnly? IncomingLetterDate);

    private static async Task<IReadOnlyList<CaseHeaderRow>> HeadersAsync(
        NpgsqlConnection connection,
        ActorAuthority authority,
        DateTimeOffset now,
        string? predicate,
        CancellationToken cancellationToken,
        Action<NpgsqlCommand>? parameters = null)
    {
        var sql = $"""
            SELECT c.id, c.case_number, c.title, c.subject, c.lifecycle_state, c.registered_at, c.created_at, c.notes,
                   c.is_restricted, c.closed_at, c.hold_until, c.hold_reason_note,
                   o.id AS requester_id, o.official_name AS requester_official, o.short_name AS requester_short,
                   cu.id AS created_by_id, cu.display_name AS created_by_name,
                   resp.responsible_id, resp.responsible_name,
                   ini.letter_number AS incoming_letter_number, ini.letter_date AS incoming_letter_date
            FROM rcs.case_record AS c
            JOIN rcs.organization AS o ON o.id = c.requesting_organization_id
            JOIN rcs.app_user AS cu ON cu.id = c.created_by_user_id
            LEFT JOIN LATERAL (
                SELECT u.id AS responsible_id, u.display_name AS responsible_name
                FROM rcs.assignment AS a
                JOIN rcs.assignment_role AS ar ON ar.id = a.assignment_role_id AND ar.code = 'RESPONSIBLE'
                JOIN rcs.app_user AS u ON u.id = a.assignee_user_id
                WHERE a.case_id = c.id AND a.status = 'ACTIVE' AND a.valid_from <= @now AND (a.valid_until IS NULL OR a.valid_until > @now)
                ORDER BY a.valid_from DESC
                LIMIT 1) AS resp ON true
            LEFT JOIN rcs.correspondence AS ini
                   ON ini.case_id = c.id AND ini.correspondence_kind_id = (SELECT id FROM rcs.correspondence_kind WHERE code = 'INITIATING')
            WHERE {VisibilityPredicate}{(predicate is null ? string.Empty : $" AND {predicate}")}
            ORDER BY c.registered_at DESC, c.case_number DESC
            """;

        await using var command = new NpgsqlCommand(sql, connection);
        command.With("actor", authority.UserId).With("is_chief", authority.IsChiefOrAbove).With("now", now);
        parameters?.Invoke(command);

        return await command.ListAsync(reader => new CaseHeaderRow(
            reader.Uuid("id"),
            reader.Text("case_number"),
            reader.Text("title"),
            reader.TextOrNull("subject"),
            CaseGraphLoader.Organization(reader, "requester"),
            reader.UuidOrNull("responsible_id") is { } responsibleId ? new UserRef(responsibleId, reader.Text("responsible_name")) : null,
            VocabularyCodes.FromCode<CaseLifecycleState>(reader.Text("lifecycle_state")),
            reader.Instant("registered_at"),
            reader.Instant("created_at"),
            new UserRef(reader.Uuid("created_by_id"), reader.Text("created_by_name")),
            reader.TextOrNull("notes"),
            reader.Bool("is_restricted"),
            reader.InstantOrNull("closed_at"),
            reader.DateOrNull("hold_until"),
            reader.TextOrNull("hold_reason_note"),
            reader.TextOrNull("incoming_letter_number"),
            reader.DateOrNull("incoming_letter_date")), cancellationToken);
    }

    private static ProgressSnapshot SnapshotFor(CaseHeaderRow header, CaseGraph graph)
    {
        var requestIdByResponse = graph.Responses.ToDictionary(response => response.Id, response => response.RequestId);
        var responsesByRequest = graph.Responses.ToLookup(response => response.RequestId);

        var requests = graph.Requests
            .Where(request => request.CaseId == header.Id)
            .Select(request => new ProgressRequestFacts(
                request.Id,
                request.Status,
                request.Target.Name,
                request.SourceRequirementId,
                request.SentAt,
                request.DueAt,
                responsesByRequest[request.Id]
                    .Select(response => new ProgressResponseFacts(response.Id, response.Status, response.IsConclusive, response.OutcomeCode))
                    .ToArray()))
            .ToArray();

        var requirements = graph.Requirements
            .Where(requirement => requirement.CaseId == header.Id)
            .Select(requirement => new ProgressRequirementFacts(
                requirement.Id,
                requirement.Status,
                requirement.IsBlocking,
                requirement.Title,
                requirement.AddressedTo?.Name,
                requirement.RaisedBy?.Name,
                requirement.SourceResponseId is { } responseId && requestIdByResponse.TryGetValue(responseId, out var requestId) ? requestId : null,
                requirement.RaisedAt,
                requirement.DueAt,
                graph.Evidence.Count(evidence => evidence.RequirementId == requirement.Id)))
            .ToArray();

        return new ProgressSnapshot(
            new ProgressCaseFacts(header.State, header.ClosedAt, header.HoldUntil, header.HoldReason),
            requests,
            requirements);
    }

    private static async Task<IReadOnlyList<ActivityEntry>> ActivityAsync(NpgsqlConnection connection, Guid caseId, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            SELECT event_seq, recorded_at, actor_display_name_snapshot, actor_kind, action_code, entity_type, entity_id,
                   after_state, reason_note
            FROM rcs.audit_event
            WHERE case_id = @case
            ORDER BY event_seq DESC
            LIMIT 200
            """, connection);
        command.With("case", caseId);

        return await command.ListAsync(reader =>
        {
            var afterState = reader.TextOrNull("after_state");
            var (detail, status) = Describe(afterState);
            return new ActivityEntry(
                reader.Long("event_seq"),
                reader.Instant("recorded_at"),
                reader.TextOrNull("actor_display_name_snapshot"),
                VocabularyCodes.FromCode<ActorKind>(reader.Text("actor_kind")),
                reader.Text("action_code"),
                reader.Text("entity_type"),
                reader.Uuid("entity_id"),
                detail,
                status,
                reader.TextOrNull("reason_note"));
        }, cancellationToken);
    }

    /// <summary>Picks the one or two human-readable facts out of an audit payload, for the activity list.</summary>
    private static (string? Detail, string? Status) Describe(string? afterState)
    {
        if (string.IsNullOrEmpty(afterState))
        {
            return (null, null);
        }

        try
        {
            using var document = JsonDocument.Parse(afterState, JsonOptions);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return (null, null);
            }

            string? Read(string name) => document.RootElement.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

            var detail = Read("case_number") ?? Read("request_number") ?? Read("title") ?? Read("registry_number") ?? Read("letter_number") ?? Read("official_name");
            return (detail, Read("status") ?? Read("lifecycle_state"));
        }
        catch (JsonException)
        {
            return (null, null);
        }
    }

    private async Task<ActorAuthority?> AuthorityAsync(NpgsqlConnection connection, ActorContext actor, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(ActorStore.ProfileQuery(ActorStore.ById), connection);
        command.With("id", actor.UserId).With("now", timeProvider.GetUtcNow());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ActorStore.Map(reader).Authority() : null;
    }

    private static async Task<bool> IsAssignedAsync(NpgsqlConnection connection, Guid caseId, Guid userId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            SELECT EXISTS (
                SELECT 1 FROM rcs.assignment AS a
                WHERE a.status = 'ACTIVE' AND a.assignee_user_id = @user
                  AND a.valid_from <= @now AND (a.valid_until IS NULL OR a.valid_until > @now)
                  AND (a.case_id = @case
                       OR a.request_id IN (SELECT id FROM rcs.request WHERE case_id = @case)
                       OR a.requirement_id IN (SELECT id FROM rcs.requirement WHERE case_id = @case)))
            """, connection);
        command.With("user", userId).With("now", now).With("case", caseId);
        return await command.ScalarAsync<bool>(cancellationToken);
    }
}
