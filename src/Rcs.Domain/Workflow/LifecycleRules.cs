using Rcs.Domain.Vocabulary;

namespace Rcs.Domain.Workflow;

/// <summary>The facts about one request that closure guard G2 reads.</summary>
public sealed record ClosureRequestFacts(RequestStatus Status);

/// <summary>
/// What the case looks like to the closure guards (WORKFLOW.md §9.2). Every field is read from rows under the
/// case-level serialization of ADR-019, never from a cached or stored summary.
/// </summary>
/// <param name="HasIssuedFinalResult">A <c>final_result</c> in <c>ISSUED</c> exists (G3).</param>
/// <param name="ClosureTypeProducesDecision">
/// The chosen closure type produces a decision. A withdrawal, duplicate or merge does not, and G3 does not apply.
/// </param>
/// <param name="HasOrHadResponsible">The case has, or has had, a RESPONSIBLE assignment (G5).</param>
public sealed record ClosureFacts(
    CaseLifecycleState State,
    IReadOnlyCollection<ClosureRequestFacts> Requests,
    IReadOnlyCollection<RequirementFacts> Requirements,
    bool HasIssuedFinalResult,
    bool ClosureTypeProducesDecision,
    bool HasOrHadResponsible);

/// <summary>One closure guard and whether this case passes it.</summary>
/// <param name="IsOverridable">G4 is never overridable; the other four are, by the Head with a reason (§9.4).</param>
public sealed record ClosureGuard(string Code, bool Passed, bool IsOverridable);

/// <summary>Case closure and reopening (WORKFLOW.md §9, §10; DECISIONS.md ADR-012).</summary>
public static class CaseClosureRules
{
    /// <summary>
    /// Guards G1–G3 and G5 in order, evaluated together so the closure screen can show every failure at once
    /// rather than one at a time. G4 (closure metadata) is not listed: it is enforced by the command and by a
    /// database CHECK, and it can never be overridden.
    /// </summary>
    public static IReadOnlyList<ClosureGuard> Evaluate(ClosureFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);
        return
        [
            // G1 — a live blocking obligation. Non-blocking requirements hold nothing open (§9.2, ADR-012).
            new("G1", !facts.Requirements.Any(requirement => requirement.IsBlocking && !requirement.Status.IsTerminal()), true),

            // G2 — an unanswered letter to an authority is unfinished work, whatever raised it.
            new("G2", facts.Requests.All(request => request.Status.IsTerminal()), true),

            // G3 — "what was the final result" must be answerable, unless this closure type produces no decision.
            new("G3", facts.HasIssuedFinalResult || !facts.ClosureTypeProducesDecision, true),

            // G5 — a case nobody ever owned should not reach closure silently.
            new("G5", facts.HasOrHadResponsible, true),
        ];
    }

    /// <summary>
    /// Whether the case is in a state that can be closed at all. Separate from the guards: closing an already
    /// closed or cancelled case is not an override case, it is a mistake.
    /// </summary>
    public static RuleCheck CanAttemptClosure(CaseLifecycleState state) =>
        state is CaseLifecycleState.Closed or CaseLifecycleState.Cancelled
            ? RuleCheck.Fail("case.already_closed")
            : RuleCheck.Ok;

    /// <summary>
    /// Requirements that stay open on a normally closed case. They keep their true state — closure never
    /// auto-fulfils, auto-voids or auto-fails anything (WORKFLOW.md §9.2.1 obligation 2) — so the person closing
    /// must be shown them first (obligation 1) and the closed case must keep showing them (obligation 3).
    /// </summary>
    public static IEnumerable<T> UnresolvedNonBlocking<T>(IEnumerable<T> requirements, Func<T, RequirementFacts> facts)
    {
        ArgumentNullException.ThrowIfNull(facts);
        return requirements.Where(requirement => facts(requirement) is { IsBlocking: false } item && !item.Status.IsTerminal());
    }

    /// <summary>T7 (WORKFLOW.md §10.1): CLOSED → ACTIVE on the same case, with a mandatory reason.</summary>
    public static RuleCheck CanReopen(CaseLifecycleState state, string? reason) =>
        state != CaseLifecycleState.Closed ? RuleCheck.Fail("case.not_closed")
        : string.IsNullOrWhiteSpace(reason) ? RuleCheck.Fail("case.reopen_reason_required")
        : RuleCheck.Ok;
}

/// <summary>Final-result transitions F1–F5 and the issue guards D1–D4 (WORKFLOW.md §8).</summary>
public static class FinalResultRules
{
    /// <summary>F1: a draft may be started whenever the case is open to work. A draft has no effect on anything (§8.1).</summary>
    public static RuleCheck CanDraft(CaseLifecycleState state, string? summary) =>
        CaseRules.AcceptsNewWork(state) is { IsAllowed: false } closed ? closed
        : string.IsNullOrWhiteSpace(summary) ? RuleCheck.Fail("final_result.summary_required")
        : RuleCheck.Ok;

    /// <summary>
    /// F2. D1 (readiness, §7.4) and D4 (the signed decision is on file) are overridable by the Head with a mandatory
    /// reason; D2 and D3 are not overridable and are satisfied by the act of issuing, which records both the decision
    /// and the approval.
    /// </summary>
    /// <param name="isReadyForFinalResult">§7.4, computed over the whole case under the case lock.</param>
    /// <param name="readinessOverrideNote">The Head's reason for issuing over an unready case; null when not overriding.</param>
    /// <param name="activeResultDocuments">
    /// D4: ACTIVE <c>FINAL_RESULT_DOCUMENT</c> links on this result — the signed decision, each pinned to one exact version.
    /// </param>
    /// <param name="documentOverrideNote">
    /// The Head's mandatory reason for issuing before the signed decision is on file ("the document must follow",
    /// §8.3). Null when not overriding.
    /// </param>
    public static RuleCheck CanIssue(
        FinalResultStatus status,
        bool isReadyForFinalResult,
        string? readinessOverrideNote,
        int activeResultDocuments,
        string? documentOverrideNote)
    {
        if (status != FinalResultStatus.Draft)
        {
            return RuleCheck.Fail("final_result.not_draft");
        }

        if (!isReadyForFinalResult && string.IsNullOrWhiteSpace(readinessOverrideNote))
        {
            return RuleCheck.Fail("final_result.case_not_ready");
        }

        return activeResultDocuments == 0 && string.IsNullOrWhiteSpace(documentOverrideNote)
            ? RuleCheck.Fail("final_result.document_required")
            : RuleCheck.Ok;
    }

    /// <summary>F4: an issued result is withdrawn with a mandatory reason; a superseded or void one is already gone.</summary>
    public static RuleCheck CanRevoke(FinalResultStatus status, string? reason) =>
        status != FinalResultStatus.Issued ? RuleCheck.Fail("final_result.not_issued")
        : string.IsNullOrWhiteSpace(reason) ? RuleCheck.Fail("final_result.revocation_reason_required")
        : RuleCheck.Ok;
}
