namespace Rcs.Domain.Workflow;

/// <summary>
/// The department's deadline rule for outgoing requests (DECISIONS.md ADR-041, closing OB-2; WORKFLOW.md §12.1).
/// </summary>
/// <remarks>
/// Deadlines exist on outgoing requests. A requirement or a case carries a <c>due_at</c> only where one was
/// genuinely stated, so nothing here defaults those. The rule is deliberately the simplest one that is true:
/// calendar days, weekends and holidays included, from the date the letter actually went out. There is no
/// working-day calculation and no holiday table, and <c>ON_HOLD</c> pauses nothing (§12.5).
/// </remarks>
public static class RequestDeadline
{
    /// <summary>Calendar days the department allows an authority to answer.</summary>
    public const int DefaultDays = 10;

    /// <summary>
    /// The deadline suggested for a request carried by a letter sent on <paramref name="sentDate"/>. It is a
    /// suggestion: the person registering the request may change it before saving, and the saved value then
    /// stands and is never recomputed behind them. A request registered long after its letter went out is
    /// measured from that letter's actual date, so a back-dated registration is overdue immediately and truthfully.
    /// </summary>
    public static DateOnly Suggest(DateOnly sentDate) => sentDate.AddDays(DefaultDays);

    /// <summary>
    /// Whether <paramref name="dueDate"/> is exactly what the rule suggests for <paramref name="sentDate"/> —
    /// the test for recording the deadline basis as the department's own (INTERNAL) rather than leaving it unstated.
    /// </summary>
    public static bool IsDefaultFor(DateOnly sentDate, DateOnly dueDate) => Suggest(sentDate) == dueDate;
}
