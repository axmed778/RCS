using Npgsql;
using Rcs.Application.Common;
using Rcs.Application.Lifecycle;
using Rcs.Application.Persistence;
using Rcs.Domain.Vocabulary;
using Rcs.Domain.Workflow;
using Rcs.Infrastructure.Cases;
using Rcs.Infrastructure.Persistence;

namespace Rcs.Infrastructure.Lifecycle;

/// <summary>
/// The read side of closure and final results. The closure preview evaluates the same guards the command does,
/// from the same rows — it is a preview of that decision, not a second opinion about it. The command re-evaluates
/// them under the case lock, so nothing here can be relied on to stay true while the person reads the screen.
/// </summary>
internal sealed class PostgresLifecycleQueries(NpgsqlDataSource dataSource, IUnitOfWorkFactory unitOfWork) : ILifecycleQueries
{
    public async Task<CommandResult<ClosurePreview>> GetClosurePreviewAsync(
        ActorContext actor, Guid caseId, string closureTypeCode, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        await using var work = (PostgresUnitOfWork)await unitOfWork.BeginAsync(cancellationToken);

        var caseRow = await CaseSql.LoadAsync(work, caseId, cancellationToken);
        if (caseRow is null)
        {
            return CommandResult<ClosurePreview>.Failure(CommandErrorKind.NotFound, "notfound.case");
        }

        var closureType = await LifecycleSql.ClosureTypeAsync(work, closureTypeCode ?? string.Empty, cancellationToken);
        if (closureType is null)
        {
            return CommandResult<ClosurePreview>.Failure(CommandErrorKind.Validation, "validation.required", nameof(closureTypeCode));
        }

        var (facts, requirements, openRequests) = await LifecycleSql.ClosureFactsAsync(work, caseRow, closureType.ProducesDecision, cancellationToken);
        var guards = CaseClosureRules.Evaluate(facts)
            .Select(guard => new ClosureGuardView(guard.Code, guard.Passed, guard.IsOverridable))
            .ToArray();

        var open = requirements.Where(requirement => !requirement.Status.IsTerminal())
            .Select(requirement => new UnresolvedItemView(requirement.Id, requirement.Title, requirement.IsBlocking, requirement.Status))
            .OrderBy(item => item.Title, StringComparer.Ordinal)
            .ToArray();

        return CommandResult<ClosurePreview>.Success(new ClosurePreview(
            caseRow.Id,
            caseRow.State,
            guards,
            open.Where(item => !item.IsBlocking).ToArray(),
            open.Where(item => item.IsBlocking).ToArray(),
            openRequests,
            facts.HasIssuedFinalResult,
            caseRow.RowVersion));
    }

    public async Task<IReadOnlyList<FinalResultView>> ListFinalResultsAsync(ActorContext actor, Guid caseId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        return await new NpgsqlCommand("""
                SELECT f.id, f.result_number, dt.code AS decision_type_code, f.summary, f.reasoning, f.status, f.row_version,
                       f.decided_at, f.approved_at, f.issued_at, f.supersedes_final_result_id, f.revoked_at, f.revocation_reason_note,
                       du.display_name AS decided_by_name, au.display_name AS approved_by_name
                FROM rcs.final_result AS f
                JOIN rcs.decision_type AS dt ON dt.id = f.decision_type_id
                LEFT JOIN rcs.app_user AS du ON du.id = f.decided_by_user_id
                LEFT JOIN rcs.app_user AS au ON au.id = f.approved_by_user_id
                WHERE f.case_id = @case
                ORDER BY f.created_at DESC
                """, connection)
            .With("case", caseId)
            .ListAsync(reader => new FinalResultView(
                reader.Uuid("id"),
                reader.Text("result_number"),
                reader.Text("decision_type_code"),
                reader.Text("summary"),
                reader.TextOrNull("reasoning"),
                VocabularyCodes.FromCode<FinalResultStatus>(reader.Text("status")),
                reader.Int("row_version"),
                reader.InstantOrNull("decided_at"),
                reader.TextOrNull("decided_by_name"),
                reader.InstantOrNull("approved_at"),
                reader.TextOrNull("approved_by_name"),
                reader.InstantOrNull("issued_at"),
                reader.UuidOrNull("supersedes_final_result_id"),
                reader.InstantOrNull("revoked_at"),
                reader.TextOrNull("revocation_reason_note")), cancellationToken);
    }
}
