using Rcs.Application.Common;
using Rcs.Application.Idempotency;
using Rcs.Domain.Vocabulary;

namespace Rcs.Application.Lifecycle;

/// <summary>F1 — someone starts drafting the decision the case exists to produce (WORKFLOW.md §8.1, §8.2).</summary>
/// <param name="SupersedesFinalResultId">
/// For a correction or amendment: the case's currently ISSUED result, which this one replaces atomically at issue
/// (WORKFLOW.md §8.6). Null for a first result.
/// </param>
public sealed record DraftFinalResultCommand(
    OperationId OperationId,
    Guid CaseId,
    string DecisionTypeCode,
    string Summary,
    string? Reasoning,
    Guid? SupersedesFinalResultId);

/// <summary>
/// F2 — the Head issues the result, recording the decision and the one approval in a single act (ADR-040). When the
/// draft names a result to supersede, F3 retires that result in the same transaction, before this one is issued.
/// </summary>
/// <param name="ReadinessOverrideNote">
/// The Head's mandatory reason for issuing over guard D1 when the case is not ready (WORKFLOW.md §8.3, §9.4). Null
/// when not overriding; a note is ignored when the case is ready anyway.
/// </param>
public sealed record IssueFinalResultCommand(Guid CaseId, Guid FinalResultId, int RowVersion, string? ReadinessOverrideNote);

/// <summary>F4 — the decision is withdrawn without a replacement, with a mandatory reason.</summary>
public sealed record RevokeFinalResultCommand(Guid CaseId, Guid FinalResultId, int RowVersion, string Reason);

/// <summary>
/// The department records that its work on this dossier is finished (WORKFLOW.md §9.3: never automatic).
/// </summary>
/// <param name="AcknowledgeUnresolvedNonBlocking">
/// The person has been shown the open non-blocking requirements and is closing anyway (WORKFLOW.md §9.2.1
/// obligation 1). Without it the closure is refused, so the confirmation cannot be skipped by posting the form.
/// Those requirements keep their true state either way — closure resolves nothing.
/// </param>
/// <param name="OverrideNote">
/// The Head's mandatory reason naming each overridden guard (§9.4). Guards G1, G2, G3 and G5 are overridable; G4
/// never is. Unresolved records are left exactly as they are.
/// </param>
public sealed record CloseCaseCommand(
    Guid CaseId,
    int RowVersion,
    string ClosureTypeCode,
    string? Note,
    bool AcknowledgeUnresolvedNonBlocking,
    string? OverrideNote);

/// <summary>T7 — the same case returns to ACTIVE, with a mandatory reason (WORKFLOW.md §10).</summary>
public sealed record ReopenCaseCommand(Guid CaseId, int RowVersion, string Reason);

/// <summary>One closure guard as the closure screen shows it.</summary>
public sealed record ClosureGuardView(string Code, bool Passed, bool IsOverridable);

/// <summary>A requirement left open on the case, named on the closure screen and on the closed case (§9.2.1).</summary>
public sealed record UnresolvedItemView(Guid Id, string Title, bool IsBlocking, RequirementStatus Status);

/// <summary>
/// What the closure screen needs in order to make closure a deliberate confirmation rather than a click: the guards,
/// what is still open, and whether this actor could override a failing guard.
/// </summary>
public sealed record ClosurePreview(
    Guid CaseId,
    CaseLifecycleState State,
    IReadOnlyList<ClosureGuardView> Guards,
    IReadOnlyList<UnresolvedItemView> UnresolvedNonBlocking,
    IReadOnlyList<UnresolvedItemView> UnresolvedBlocking,
    int OpenRequests,
    bool HasIssuedFinalResult,
    int RowVersion)
{
    /// <summary>Guards that fail for the closure type the screen is currently showing.</summary>
    public IReadOnlyList<ClosureGuardView> Failing => Guards.Where(guard => !guard.Passed).ToArray();
}

/// <summary>A final result of the case, as the workspace shows it.</summary>
public sealed record FinalResultView(
    Guid Id,
    string ResultNumber,
    string DecisionTypeCode,
    string Summary,
    string? Reasoning,
    FinalResultStatus Status,
    int RowVersion,
    DateTimeOffset? DecidedAt,
    string? DecidedByName,
    DateTimeOffset? ApprovedAt,
    string? ApprovedByName,
    DateTimeOffset? IssuedAt,
    Guid? SupersedesFinalResultId,
    DateTimeOffset? RevokedAt,
    string? RevocationReasonNote);

public interface ICaseLifecycleService
{
    Task<CommandResult<Guid>> DraftFinalResultAsync(ActorContext actor, DraftFinalResultCommand command, CancellationToken cancellationToken = default);

    Task<CommandResult<Guid>> IssueFinalResultAsync(ActorContext actor, IssueFinalResultCommand command, CancellationToken cancellationToken = default);

    Task<CommandResult<Guid>> RevokeFinalResultAsync(ActorContext actor, RevokeFinalResultCommand command, CancellationToken cancellationToken = default);

    Task<CommandResult<Guid>> CloseCaseAsync(ActorContext actor, CloseCaseCommand command, CancellationToken cancellationToken = default);

    Task<CommandResult<Guid>> ReopenCaseAsync(ActorContext actor, ReopenCaseCommand command, CancellationToken cancellationToken = default);
}

public interface ILifecycleQueries
{
    /// <summary>The guards and open items for one case, evaluated for the given closure type.</summary>
    Task<CommandResult<ClosurePreview>> GetClosurePreviewAsync(ActorContext actor, Guid caseId, string closureTypeCode, CancellationToken cancellationToken = default);

    /// <summary>Every final result of the case, newest first. Superseded and revoked ones stay fully readable.</summary>
    Task<IReadOnlyList<FinalResultView>> ListFinalResultsAsync(ActorContext actor, Guid caseId, CancellationToken cancellationToken = default);
}
