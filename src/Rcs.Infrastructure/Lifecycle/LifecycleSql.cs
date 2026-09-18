using Rcs.Domain.Progress;
using Rcs.Domain.Vocabulary;
using Rcs.Domain.Workflow;
using Rcs.Infrastructure.Cases;
using Rcs.Infrastructure.Persistence;

namespace Rcs.Infrastructure.Lifecycle;

internal sealed record FinalResultRow(Guid Id, Guid CaseId, FinalResultStatus Status, int RowVersion, Guid? SupersedesFinalResultId, Guid? DecidedByUserId);

internal sealed record ClosureTypeRow(Guid Id, string Code, bool ProducesDecision);

/// <summary>
/// Reads the closure guards and final-result rows need, all inside the caller's transaction and therefore under the
/// case lock (WORKFLOW.md §9.2: "guards are evaluated under serialization").
/// </summary>
internal static class LifecycleSql
{
    public static Task<FinalResultRow?> LoadAsync(PostgresUnitOfWork unitOfWork, Guid finalResultId, CancellationToken cancellationToken) =>
        unitOfWork.Command("SELECT id, case_id, status, row_version, supersedes_final_result_id, decided_by_user_id FROM rcs.final_result WHERE id = @id")
            .With("id", finalResultId)
            .SingleOrDefaultAsync(reader => new FinalResultRow(
                reader.Uuid("id"),
                reader.Uuid("case_id"),
                VocabularyCodes.FromCode<FinalResultStatus>(reader.Text("status")),
                reader.Int("row_version"),
                reader.UuidOrNull("supersedes_final_result_id"),
                reader.UuidOrNull("decided_by_user_id")), cancellationToken);

    public static Task<ClosureTypeRow?> ClosureTypeAsync(PostgresUnitOfWork unitOfWork, string code, CancellationToken cancellationToken) =>
        unitOfWork.Command("SELECT id, code, produces_decision FROM rcs.closure_type WHERE code = @code AND is_active")
            .With("code", code)
            .SingleOrDefaultAsync(reader => new ClosureTypeRow(reader.Uuid("id"), reader.Text("code"), reader.Bool("produces_decision")), cancellationToken);

    public static Task<Guid?> IssuedFinalResultIdAsync(PostgresUnitOfWork unitOfWork, Guid caseId, CancellationToken cancellationToken) =>
        unitOfWork.Command("SELECT id FROM rcs.final_result WHERE case_id = @case AND status = 'ISSUED'")
            .With("case", caseId)
            .ScalarAsync<Guid?>(cancellationToken);

    /// <summary>G5: the case has, or has had, a RESPONSIBLE assignment — ended and voided ones still count as "has had".</summary>
    public static Task<bool> HasOrHadResponsibleAsync(PostgresUnitOfWork unitOfWork, Guid caseId, CancellationToken cancellationToken) =>
        unitOfWork.Command("""
                SELECT EXISTS (
                    SELECT 1 FROM rcs.assignment AS a
                    JOIN rcs.assignment_role AS ar ON ar.id = a.assignment_role_id AND ar.code = 'RESPONSIBLE'
                    WHERE a.case_id = @case AND a.status <> 'VOID')
                """)
            .With("case", caseId)
            .ScalarAsync<bool>(cancellationToken);

    /// <summary>The next result number for the case. Inside the case lock, so the count cannot race. PROVISIONAL format (OQ-7).</summary>
    public static async Task<string> NextResultNumberAsync(PostgresUnitOfWork unitOfWork, string caseNumber, Guid caseId, CancellationToken cancellationToken)
    {
        var count = await unitOfWork.Command("SELECT count(*) FROM rcs.final_result WHERE case_id = @case")
            .With("case", caseId).ScalarAsync<long>(cancellationToken);
        return $"{caseNumber}-N{count + 1}";
    }

    /// <summary>
    /// The facts guards G1–G3 and G5 read, plus the requirement rows the closure screen names. Read through the same
    /// loader the workspace uses, so a guard and the screen can never disagree about what is open.
    /// </summary>
    public static async Task<(ClosureFacts Facts, IReadOnlyList<RequirementRowFull> Requirements, int OpenRequests)> ClosureFactsAsync(
        PostgresUnitOfWork unitOfWork,
        CaseRow caseRow,
        bool closureTypeProducesDecision,
        CancellationToken cancellationToken)
    {
        var graph = await CaseGraphLoader.LoadAsync(unitOfWork.Connection, unitOfWork.Transaction, [caseRow.Id], includeLetters: false, cancellationToken);
        var hasIssued = await IssuedFinalResultIdAsync(unitOfWork, caseRow.Id, cancellationToken) is not null;
        var hasResponsible = await HasOrHadResponsibleAsync(unitOfWork, caseRow.Id, cancellationToken);

        var facts = new ClosureFacts(
            caseRow.State,
            graph.Requests.Select(request => new ClosureRequestFacts(request.Status)).ToArray(),
            graph.Requirements.Select(requirement => new RequirementFacts(requirement.Status, requirement.IsBlocking)).ToArray(),
            hasIssued,
            closureTypeProducesDecision,
            hasResponsible);

        return (facts, graph.Requirements, graph.Requests.Count(request => !request.Status.IsTerminal()));
    }

    /// <summary>
    /// Readiness for the final result (§7.4, guard D1), derived by the same evaluator the workspace uses rather than
    /// by a second reading of the same rules.
    /// </summary>
    /// <param name="supersedesFinalResultId">
    /// The result this draft replaces, when it is a replacement. Readiness condition 5 — no result already ISSUED —
    /// does not apply to it: an issued result never blocks its own replacement (WORKFLOW.md §7.4, §8.6). Whether the
    /// named result really is the one in force is checked separately at issue, and again after this.
    /// </param>
    public static async Task<bool> IsReadyForFinalResultAsync(
        PostgresUnitOfWork unitOfWork,
        CaseRow caseRow,
        ProgressEvaluator progress,
        DateTimeOffset now,
        Guid? supersedesFinalResultId,
        CancellationToken cancellationToken)
    {
        var graph = await CaseGraphLoader.LoadAsync(unitOfWork.Connection, unitOfWork.Transaction, [caseRow.Id], includeLetters: false, cancellationToken);
        var inForce = await IssuedFinalResultIdAsync(unitOfWork, caseRow.Id, cancellationToken);
        var blocksReadiness = inForce is { } issued && issued != supersedesFinalResultId;
        return progress.Evaluate(SnapshotFor(caseRow, graph, blocksReadiness), now).IsReadyForFinalResult;
    }

    /// <summary>The progress snapshot of one case, from rows already loaded under the lock.</summary>
    public static ProgressSnapshot SnapshotFor(CaseRow caseRow, CaseGraph graph, bool hasIssuedFinalResult)
    {
        var requestIdByResponse = graph.Responses.ToDictionary(response => response.Id, response => response.RequestId);
        var responsesByRequest = graph.Responses.ToLookup(response => response.RequestId);

        return new ProgressSnapshot(
            new ProgressCaseFacts(caseRow.State, null, null, null, null, new ProgressFinalResultFacts(HasIssued: hasIssuedFinalResult)),
            graph.Requests.Select(request => new ProgressRequestFacts(
                request.Id,
                request.Status,
                request.Target.Name,
                request.SourceRequirementId,
                request.SentAt,
                request.DueAt,
                responsesByRequest[request.Id]
                    .Select(response => new ProgressResponseFacts(response.Id, response.Status, response.IsConclusive, response.OutcomeCode))
                    .ToArray())).ToArray(),
            graph.Requirements.Select(requirement => new ProgressRequirementFacts(
                requirement.Id,
                requirement.Status,
                requirement.IsBlocking,
                requirement.Title,
                requirement.AddressedTo?.Name,
                requirement.RaisedBy?.Name,
                requirement.SourceResponseId is { } responseId && requestIdByResponse.TryGetValue(responseId, out var requestId) ? requestId : null,
                requirement.RaisedAt,
                requirement.DueAt,
                graph.Evidence.Count(evidence => evidence.RequirementId == requirement.Id))).ToArray());
    }
}
