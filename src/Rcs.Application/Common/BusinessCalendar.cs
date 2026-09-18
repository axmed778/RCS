namespace Rcs.Application.Common;

/// <summary>
/// Converts between business dates (dates printed on letters and typed into forms) and stored instants, in the
/// department's configured time zone. Every date↔instant conversion goes through here, so no code relies on a
/// server or session time zone (database/README.md, "Time").
/// </summary>
/// <remarks>
/// Deadlines entered as a date mean "by the end of that business day": they are stored as the last microsecond
/// of the day, so a deadline is overdue from the following day (WORKFLOW.md §12.2). Other business dates are
/// stored as the start of the day. The rules for computing deadlines themselves are OB-2 / OQ-5 and are not
/// decided here.
/// </remarks>
public sealed class BusinessCalendar(TimeZoneInfo timeZone, TimeProvider timeProvider)
{
    public TimeZoneInfo TimeZone { get; } = timeZone;

    public DateTimeOffset Now => timeProvider.GetUtcNow();

    public DateOnly Today => ToDate(Now);

    public DateTimeOffset StartOfDay(DateOnly date)
    {
        var local = date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
        return new DateTimeOffset(local, TimeZone.GetUtcOffset(local)).ToUniversalTime();
    }

    public DateTimeOffset EndOfDay(DateOnly date) => StartOfDay(date.AddDays(1)).AddTicks(-TimeSpan.TicksPerMicrosecond);

    public DateOnly ToDate(DateTimeOffset instant) => DateOnly.FromDateTime(ToLocal(instant).DateTime);

    public DateTimeOffset ToLocal(DateTimeOffset instant) => TimeZoneInfo.ConvertTime(instant, TimeZone);
}
