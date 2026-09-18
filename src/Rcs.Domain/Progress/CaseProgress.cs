using Rcs.Domain.Vocabulary;
using Rcs.Domain.Workflow;

namespace Rcs.Domain.Progress;

// Derived operational progress (DECISIONS.md ADR-029; WORKFLOW.md §7.2, §7.4, §11, §12). Nothing here is
// stored: progress is computed from the case's rows every time it is shown.

public sealed record ProgressCaseFacts(CaseLifecycleState State, DateTimeOffset? ClosedAt, DateOnly? HoldUntil, string? HoldReason);

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

public sealed record CaseProgress(
    ProgressMessage Headline,
    ProgressSummary Summary,
    bool IsReadyForFinalResult,
    IReadOnlyDictionary<Guid, BranchProgress> Branches,
    IReadOnlyDictionary<Guid, RequestProgress> Requests,
    IReadOnlyDictionary<Guid, RequirementProgress> Requirements);
