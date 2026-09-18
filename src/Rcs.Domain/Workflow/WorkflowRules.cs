using Rcs.Domain.Vocabulary;

namespace Rcs.Domain.Workflow;

/// <summary>The outcome of a workflow guard: allowed, or refused with a stable message code.</summary>
public readonly record struct RuleCheck(string? ViolationCode)
{
    public static readonly RuleCheck Ok = new(null);

    public bool IsAllowed => ViolationCode is null;

    public static RuleCheck Fail(string code) => new(code);
}

/// <summary>The facts about one response that request rules read.</summary>
public sealed record ResponseFacts(ResponseStatus Status, bool IsConclusive, string OutcomeCode);

/// <summary>The facts about one requirement that request and closure rules read.</summary>
public sealed record RequirementFacts(RequirementStatus Status, bool IsBlocking);

/// <summary>Case lifecycle rules used by the first vertical slice (WORKFLOW.md §1, §9.1).</summary>
public static class CaseRules
{
    /// <summary>
    /// New work (requests, requirements, responses that create work) is not added to a CLOSED or CANCELLED case;
    /// a closed case is reopened first (WORKFLOW.md §9.1, §4.6).
    /// </summary>
    public static RuleCheck AcceptsNewWork(CaseLifecycleState state) =>
        state is CaseLifecycleState.Closed or CaseLifecycleState.Cancelled
            ? RuleCheck.Fail("case.not_open_to_work")
            : RuleCheck.Ok;

    /// <summary>
    /// T2: REGISTERED → ACTIVE is a system consequence of the first substantive record, guarded by a current
    /// RESPONSIBLE assignment (WORKFLOW.md §1.1).
    /// </summary>
    public static bool ActivatesOnSubstantiveRecord(CaseLifecycleState state, bool hasCurrentResponsible) =>
        state == CaseLifecycleState.Registered && hasCurrentResponsible;
}

/// <summary>Request rules (DOMAIN_MODEL.md §2.9; WORKFLOW.md §3).</summary>
public static class RequestRules
{
    public static bool HasActiveConclusive(IEnumerable<ResponseFacts> responses) =>
        responses.Any(response => response.Status == ResponseStatus.Active && response.IsConclusive);

    /// <summary>
    /// An unresolved conflict: more than one ACTIVE conclusive response with different outcomes and no supersession
    /// between them (WORKFLOW.md §4.5). Supersession moves a response out of ACTIVE, so ACTIVE rows are the ones in force.
    /// </summary>
    public static bool HasConflict(IEnumerable<ResponseFacts> responses) =>
        responses.Where(response => response.Status == ResponseStatus.Active && response.IsConclusive)
            .Select(response => response.OutcomeCode)
            .Distinct(StringComparer.Ordinal)
            .Skip(1)
            .Any();

    /// <summary>
    /// The frozen closure rule (WORKFLOW.md §3.4): ANSWERED, an ACTIVE conclusive response, no unresolved conflict,
    /// and every BLOCKING requirement raised by this request's responses terminal. Closure itself is always a person's act.
    /// </summary>
    public static RuleCheck CanClose(RequestStatus status, IReadOnlyCollection<ResponseFacts> responses, IEnumerable<RequirementFacts> requirementsRaisedByItsResponses)
    {
        if (status != RequestStatus.Answered)
        {
            return RuleCheck.Fail("request.not_answered");
        }

        if (!HasActiveConclusive(responses))
        {
            return RuleCheck.Fail("request.no_conclusive_response");
        }

        if (HasConflict(responses))
        {
            return RuleCheck.Fail("request.conflicting_responses");
        }

        return requirementsRaisedByItsResponses.Any(requirement => requirement.IsBlocking && !requirement.Status.IsTerminal())
            ? RuleCheck.Fail("request.blocking_requirements_open")
            : RuleCheck.Ok;
    }

    /// <summary>
    /// The request state that follows mechanically from its responses and requirements (WORKFLOW.md §3.2 R3, R7, R8, R9).
    /// Never closes a request: R4 is a human act.
    /// </summary>
    public static RequestStatus Consequence(RequestStatus current, IReadOnlyCollection<ResponseFacts> responses, IEnumerable<RequirementFacts> requirementsRaisedByItsResponses)
    {
        var conclusive = HasActiveConclusive(responses);
        return current switch
        {
            RequestStatus.Sent when conclusive => RequestStatus.Answered,                                     // R3
            RequestStatus.Answered when !conclusive => RequestStatus.Sent,                                     // R7
            RequestStatus.Closed when !conclusive => RequestStatus.Sent,                                       // R8
            RequestStatus.Closed when HasConflict(responses)
                || requirementsRaisedByItsResponses.Any(r => r.IsBlocking && !r.Status.IsTerminal()) => RequestStatus.Answered, // R9
            _ => current,
        };
    }

    /// <summary>A child request may be created only from a requirement still open to work (WORKFLOW.md §5.5, §6).</summary>
    public static RuleCheck CanSpawnChildRequest(RequirementStatus sourceRequirementStatus) =>
        sourceRequirementStatus is RequirementStatus.Open or RequirementStatus.InProgress
            ? RuleCheck.Ok
            : RuleCheck.Fail("requirement.not_open_for_child_request");
}

/// <summary>Requirement transitions Q2–Q6 (WORKFLOW.md §5.2; DOMAIN_MODEL.md §2.11).</summary>
public static class RequirementRules
{
    /// <summary>Q2, explicit or as the consequence of a child request reaching SENT: OPEN → IN_PROGRESS.</summary>
    public static RuleCheck CanStart(RequirementStatus status) =>
        status == RequirementStatus.Open ? RuleCheck.Ok : RuleCheck.Fail("requirement.not_open");

    public static bool IsResolvable(RequirementStatus status) =>
        status is RequirementStatus.Open or RequirementStatus.InProgress;

    /// <summary>Q3: at least one ACTIVE evidence row or an explicit resolution note.</summary>
    public static RuleCheck CanFulfill(RequirementStatus status, int activeEvidenceCount, string? resolutionNote)
    {
        if (!IsResolvable(status))
        {
            return RuleCheck.Fail("requirement.not_resolvable");
        }

        return activeEvidenceCount > 0 || !string.IsNullOrWhiteSpace(resolutionNote)
            ? RuleCheck.Ok
            : RuleCheck.Fail("requirement.fulfil_needs_evidence_or_note");
    }

    /// <summary>Q4: authoriser, waiver reason and note are all mandatory.</summary>
    public static RuleCheck CanWaive(RequirementStatus status, string? waiverReasonCode, string? note) =>
        !IsResolvable(status) ? RuleCheck.Fail("requirement.not_resolvable")
        : string.IsNullOrWhiteSpace(waiverReasonCode) ? RuleCheck.Fail("requirement.waiver_reason_required")
        : string.IsNullOrWhiteSpace(note) ? RuleCheck.Fail("requirement.note_required")
        : RuleCheck.Ok;

    /// <summary>Q5: a void reason and a note; the later response that removed the need is optional.</summary>
    public static RuleCheck CanVoid(RequirementStatus status, string? voidReasonCode, string? note) =>
        !IsResolvable(status) ? RuleCheck.Fail("requirement.not_resolvable")
        : string.IsNullOrWhiteSpace(voidReasonCode) ? RuleCheck.Fail("requirement.void_reason_required")
        : string.IsNullOrWhiteSpace(note) ? RuleCheck.Fail("requirement.note_required")
        : RuleCheck.Ok;

    /// <summary>Q6: a failure reason is mandatory.</summary>
    public static RuleCheck CanFail(RequirementStatus status, string? failureReasonNote) =>
        !IsResolvable(status) ? RuleCheck.Fail("requirement.not_resolvable")
        : string.IsNullOrWhiteSpace(failureReasonNote) ? RuleCheck.Fail("requirement.note_required")
        : RuleCheck.Ok;
}
