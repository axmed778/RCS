namespace Rcs.Domain.Vocabulary;

/// <summary>Terminal-state sets stated explicitly in the frozen documents. No other rule lives here.</summary>
public static class TerminalStates
{
    /// <summary>
    /// FULFILLED, WAIVED, VOID and FAILED (DOMAIN_MODEL.md §2.9 condition 2, §5.4). Terminal as business
    /// outcomes; only an error correction (WORKFLOW.md §5.2, Q7) returns a requirement to an open state.
    /// </summary>
    public static bool IsTerminal(this RequirementStatus status) => status switch
    {
        RequirementStatus.Open or RequirementStatus.InProgress => false,
        RequirementStatus.Fulfilled or RequirementStatus.Waived or RequirementStatus.Void or RequirementStatus.Failed => true,
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, "Unknown requirement status."),
    };

    /// <summary>CLOSED, WITHDRAWN and VOID (WORKFLOW.md §7.4 condition 3, §9.2 guard G2).</summary>
    public static bool IsTerminal(this RequestStatus status) => status switch
    {
        RequestStatus.Draft or RequestStatus.Sent or RequestStatus.Answered => false,
        RequestStatus.Closed or RequestStatus.Withdrawn or RequestStatus.Void => true,
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, "Unknown request status."),
    };
}
