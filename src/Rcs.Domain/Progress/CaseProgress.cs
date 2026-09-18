using Rcs.Domain.Vocabulary;
using Rcs.Domain.Workflow;

namespace Rcs.Domain.Progress;

// Derived operational progress (DECISIONS.md ADR-029; WORKFLOW.md §7.2, §7.4, §11, §12). Nothing here is
// stored: progress is computed from the case's rows every time it is shown.

/// <summary>
/// What the case's final results contribute to derived progress (WORKFLOW.md §11.3 rows P1, P5, P15, P17 and
/// readiness condition 5 of §7.4). Only the derived facts, never the result rows themselves.
/// </summary>
/// <param name="HasDraftAwaitingApproval">
/// A DRAFT whose decision is recorded but whose approval is not — "awaiting approval" is derived, never a stored
/// state (WORKFLOW.md §8.4).
/// </param>
public sealed record ProgressFinalResultFacts(
    bool HasIssued = false,
    bool HasDraftAwaitingApproval = false,
    DateTimeOffset? IssuedAt = null,
    string? IssuedDecisionTypeCode = null);

public sealed record ProgressCaseFacts(
    CaseLifecycleState State,
    DateTimeOffset? ClosedAt,
    DateOnly? HoldUntil,
    string? HoldReason,
    string? ClosureTypeCode = null,
    ProgressFinalResultFacts? FinalResult = null)
{
    public ProgressFinalResultFacts Result => FinalResult ?? new ProgressFinalResultFacts();
}

public sealed record ProgressResponseFacts(Guid Id, ResponseStatus Status, bool IsConclusive, string OutcomeCode);

public sealed record ProgressRequestFacts(
    Guid Id,
    RequestStatus Status,
    string TargetName,
    Guid? SourceRequirementId,
    DateTimeOffset? SentAt,
    DateTimeOffset? DueAt,
    IReadOnlyList<ProgressResponseFacts> Responses);

/// <param name="SourceRequestId">The request whose response raised this requirement; null for non-response origins.</param>
public sealed record ProgressRequirementFacts(
    Guid Id,
    RequirementStatus Status,
    bool IsBlocking,
    string Title,
    string? AddressedToName,
    string? RaisedByName,
    Guid? SourceRequestId,
    DateTimeOffset RaisedAt,
    DateTimeOffset? DueAt,
    int ActiveEvidenceCount);

public sealed record ProgressSnapshot(
    ProgressCaseFacts Case,
    IReadOnlyList<ProgressRequestFacts> Requests,
    IReadOnlyList<ProgressRequirementFacts> Requirements);

/// <summary>
/// A message identified by the precedence-ladder row that produced it (WORKFLOW.md §11.3), with its arguments.
/// Wording is presentation and is localised by the user interface from <see cref="Code"/>.
/// </summary>
public sealed record ProgressMessage(string Code, IReadOnlyList<string> Arguments)
{
    public static ProgressMessage Of(string code, params string[] arguments) => new(code, arguments);
}

/// <summary>WORKFLOW.md §7.2, first match wins. <see cref="Answered"/> is the display fallback when no row matches.</summary>
public enum BranchState
{
    Failed = 1,
    Blocked,
    WaitingExternal,
    WaitingInternal,
    Withdrawn,
    Complete,
    Answered,
}

public enum RequestProgressState
{
    Draft = 1,
    AwaitingResponse,
    PartiallyAnswered,

    /// <summary>Out, unanswered, and today is the last day of the deadline (WORKFLOW.md §12.2).</summary>
    DueToday,
    Overdue,
    Conflict,
    AnsweredCloseable,
    AnsweredWithOpenBlocking,
    Closed,
    Withdrawn,
    Void,
}

public enum RequirementProgressState
{
    /// <summary>Open with no child request awaiting an answer and no evidence yet: the department owes the next move (B2).</summary>
    OursToAct = 1,

    /// <summary>A child request is out and unanswered.</summary>
    WaitingExternal,
    Fulfilled,
    Waived,
    Void,
    Failed,
}

public sealed record RequestProgress(RequestProgressState State, int OverdueDays);

public sealed record RequirementProgress(RequirementProgressState State, int OverdueDays);

public sealed record BranchProgress(Guid TopLevelRequestId, BranchState State, ProgressMessage? Headline);

/// <summary>The summary line (WORKFLOW.md §11.4): always shown, never the headline.</summary>
public sealed record ProgressSummary(int Branches, int RequestsConsidered, int RequestsAnswered, int OpenRequirements, int Overdue, int Waived, int Failed);

/// <param name="ClosedWithUnresolvedItems">
/// The case is CLOSED and something in it is still open — non-blocking requirements left at a normal closure
/// (WORKFLOW.md §9.2.1 obligation 3) or anything left by a Head override (§9.4). Derived, never stored, and the
/// reason nothing had to be falsified to close.
/// </param>
/// <param name="UnresolvedAtClosure">How many such items there are, for the closed case's own headline.</param>
public sealed record CaseProgress(
    ProgressMessage Headline,
    ProgressSummary Summary,
    bool IsReadyForFinalResult,
    IReadOnlyDictionary<Guid, BranchProgress> Branches,
    IReadOnlyDictionary<Guid, RequestProgress> Requests,
    IReadOnlyDictionary<Guid, RequirementProgress> Requirements,
    bool ClosedWithUnresolvedItems = false,
    int UnresolvedAtClosure = 0);
