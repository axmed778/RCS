using Rcs.Domain.Vocabulary;
using Rcs.Domain.Workflow;
using Rcs.Infrastructure.Audit;
using Rcs.Infrastructure.Commands;
using Rcs.Infrastructure.Persistence;

namespace Rcs.Infrastructure.Cases;

internal sealed record CaseRow(Guid Id, string CaseNumber, CaseLifecycleState State, bool IsRestricted, Guid RequestingOrganizationId, int RowVersion);

/// <summary>Case reads and the case-level system consequences shared by several commands.</summary>
internal static class CaseSql
{
    public static Task<CaseRow?> LoadAsync(PostgresUnitOfWork unitOfWork, Guid caseId, CancellationToken cancellationToken) =>
        unitOfWork.Command("""
                SELECT id, case_number, lifecycle_state, is_restricted, requesting_organization_id, row_version
                FROM rcs.case_record WHERE id = @id
                """)
            .With("id", caseId)
            .SingleOrDefaultAsync(
                reader => new CaseRow(
                    reader.Uuid("id"),
                    reader.Text("case_number"),
                    VocabularyCodes.FromCode<CaseLifecycleState>(reader.Text("lifecycle_state")),
                    reader.Bool("is_restricted"),
                    reader.Uuid("requesting_organization_id"),
                    reader.Int("row_version")),
                cancellationToken);

    /// <summary>
    /// An ACTIVE assignment of any role — including temporary cover inside its window — on the case, or on one of its
    /// requests or requirements (PERMISSIONS.md §21.2).
    /// </summary>
    public static async Task<bool> ActorIsAssignedAsync(PostgresUnitOfWork unitOfWork, Guid caseId, Guid userId, DateTimeOffset now, CancellationToken cancellationToken) =>
        await unitOfWork.Command("""
                SELECT EXISTS (
                    SELECT 1 FROM rcs.assignment AS a
                    WHERE a.status = 'ACTIVE' AND a.assignee_user_id = @user
                      AND a.valid_from <= @now AND (a.valid_until IS NULL OR a.valid_until > @now)
                      AND (a.case_id = @case
                           OR a.request_id IN (SELECT id FROM rcs.request WHERE case_id = @case)
                           OR a.requirement_id IN (SELECT id FROM rcs.requirement WHERE case_id = @case)))
                """)
            .With("user", userId)
            .With("now", now)
            .With("case", caseId)
            .ScalarAsync<bool>(cancellationToken);

    public static async Task<bool> HasCurrentResponsibleAsync(PostgresUnitOfWork unitOfWork, Guid caseId, DateTimeOffset now, CancellationToken cancellationToken) =>
        await unitOfWork.Command("""
                SELECT EXISTS (
                    SELECT 1 FROM rcs.assignment AS a
                    JOIN rcs.assignment_role AS ar ON ar.id = a.assignment_role_id AND ar.code = 'RESPONSIBLE'
                    WHERE a.case_id = @case AND a.status = 'ACTIVE'
                      AND a.valid_from <= @now AND (a.valid_until IS NULL OR a.valid_until > @now))
                """)
            .With("case", caseId)
            .With("now", now)
            .ScalarAsync<bool>(cancellationToken);

    public static Task<Guid?> OwnOrganizationIdAsync(PostgresUnitOfWork unitOfWork, CancellationToken cancellationToken) =>
        unitOfWork.Command("SELECT id FROM rcs.organization WHERE is_own_organization").ScalarAsync<Guid?>(cancellationToken);

    /// <summary>
    /// T2 (WORKFLOW.md §1.1): REGISTERED → ACTIVE as a system consequence of the first substantive record, only when a
    /// RESPONSIBLE assignment exists. Written as a case_state_change row and a SYSTEM audit event.
    /// </summary>
    public static async Task ActivateIfRegisteredAsync(CommandScope scope, CaseRow caseRow, CancellationToken cancellationToken)
    {
        if (caseRow.State != CaseLifecycleState.Registered)
        {
            return;
        }

        var hasResponsible = await HasCurrentResponsibleAsync(scope.UnitOfWork, caseRow.Id, scope.Now, cancellationToken);
        if (!CaseRules.ActivatesOnSubstantiveRecord(caseRow.State, hasResponsible))
        {
            return;
        }

        var version = await scope.UnitOfWork.Command("""
                UPDATE rcs.case_record
                SET lifecycle_state = 'ACTIVE', updated_at = @now, updated_by_user_id = @actor, row_version = row_version + 1
                WHERE id = @id AND lifecycle_state = 'REGISTERED'
                RETURNING row_version
                """)
            .With("now", scope.Now)
            .With("actor", scope.Actor.UserId)
            .With("id", caseRow.Id)
            .ScalarAsync<int?>(cancellationToken);
        if (version is null)
        {
            return;
        }

        await scope.UnitOfWork.Command("""
                INSERT INTO rcs.case_state_change (id, case_id, from_state, to_state, occurred_at, actor_user_id)
                VALUES (@id, @case, 'REGISTERED', 'ACTIVE', @now, @actor)
                """)
            .With("id", scope.NewId())
            .With("case", caseRow.Id)
            .With("now", scope.Now)
            .With("actor", scope.Actor.UserId)
            .ExecuteAsync(cancellationToken);

        await scope.AuditAsync(new AuditEntry(AuditActionCodes.StateChange, AuditEntityTypes.Case, caseRow.Id, version, caseRow.Id,
            Before: new { lifecycle_state = "REGISTERED" }, After: new { lifecycle_state = "ACTIVE" }, ActorKind: ActorKind.System), cancellationToken);
    }

    /// <summary>
    /// Applies the request consequences R3, R7, R8 and R9 (WORKFLOW.md §3.2) after a response or requirement changed.
    /// Never closes a request.
    /// </summary>
    public static async Task ApplyRequestConsequenceAsync(CommandScope scope, Guid requestId, Guid caseId, CancellationToken cancellationToken)
    {
        var request = await scope.UnitOfWork.Command("SELECT status, row_version FROM rcs.request WHERE id = @id")
            .With("id", requestId)
            .SingleOrDefaultAsync(reader => new { Status = VocabularyCodes.FromCode<RequestStatus>(reader.Text("status")), RowVersion = reader.Int("row_version") }, cancellationToken)
            ?? throw new InvalidOperationException("The request disappeared inside the transaction.");

        var responses = await scope.UnitOfWork.Command("""
                SELECT p.status, p.is_conclusive, o.code AS outcome
                FROM rcs.response AS p JOIN rcs.response_outcome AS o ON o.id = p.response_outcome_id
                WHERE p.request_id = @request
                """)
            .With("request", requestId)
            .ListAsync(reader => new ResponseFacts(VocabularyCodes.FromCode<ResponseStatus>(reader.Text("status")), reader.Bool("is_conclusive"), reader.Text("outcome")), cancellationToken);

        var requirements = await scope.UnitOfWork.Command("""
                SELECT q.status, q.is_blocking
                FROM rcs.requirement AS q JOIN rcs.response AS p ON p.id = q.source_response_id
                WHERE p.request_id = @request
                """)
            .With("request", requestId)
            .ListAsync(reader => new RequirementFacts(VocabularyCodes.FromCode<RequirementStatus>(reader.Text("status")), reader.Bool("is_blocking")), cancellationToken);

        var next = RequestRules.Consequence(request.Status, responses, requirements);
        if (next == request.Status)
        {
            return;
        }

        var version = await scope.UnitOfWork.Command("""
                UPDATE rcs.request
                SET status = @status, updated_at = @now, updated_by_user_id = @actor, row_version = row_version + 1
                WHERE id = @id AND row_version = @row_version
                RETURNING row_version
                """)
            .With("status", next.ToCode())
            .With("now", scope.Now)
            .With("actor", scope.Actor.UserId)
            .With("id", requestId)
            .With("row_version", request.RowVersion)
            .ScalarAsync<int?>(cancellationToken)
            ?? throw new InvalidOperationException("The request changed inside the case lock.");

        await scope.AuditAsync(new AuditEntry(AuditActionCodes.StateChange, AuditEntityTypes.Request, requestId, version, caseId,
            Before: new { status = request.Status.ToCode() }, After: new { status = next.ToCode() }, ActorKind: ActorKind.System), cancellationToken);
    }
}
