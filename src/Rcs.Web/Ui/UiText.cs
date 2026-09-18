using System.Globalization;
using Microsoft.Extensions.Localization;
using Rcs.Application.Common;
using Rcs.Domain.Progress;
using Rcs.Domain.Vocabulary;

namespace Rcs.Web.Ui;

/// <summary>
/// Presentation text and formatting. Every user-visible string comes from the resource catalogue, keyed by a stable
/// code — a database vocabulary code, a workflow ladder row, or a message code from the application layer. No business
/// rule is expressed in a translated string, so another language is another .resx and nothing else.
/// </summary>
public sealed class UiText(IStringLocalizer<SharedResource> localizer, BusinessCalendar calendar)
{
    private const string DateFormat = "dd.MM.yyyy";
    private const string DateTimeFormat = "dd.MM.yyyy HH:mm";

    public LocalizedString this[string key] => localizer[key];

    public string Format(string key, params object[] arguments) =>
        string.Format(CultureInfo.InvariantCulture, localizer[key].Value, arguments);

    /// <summary>A vocabulary code translated by its catalogue prefix, e.g. <c>CaseState.ACTIVE</c>.</summary>
    public string Code(string prefix, string? code) => string.IsNullOrEmpty(code) ? this["Common.None"].Value : localizer[$"{prefix}.{code}"].Value;

    public string CaseState(CaseLifecycleState state) => Code("CaseState", state.ToCode());

    public string RequestStatus(RequestStatus status) => Code("RequestStatus", status.ToCode());

    public string RequirementStatus(RequirementStatus status) => Code("RequirementStatus", status.ToCode());

    public string ResponseStatus(ResponseStatus status) => Code("ResponseStatus", status.ToCode());

    public string Date(DateOnly? date) => date?.ToString(DateFormat, CultureInfo.InvariantCulture) ?? this["Common.None"];

    public string Date(DateTimeOffset? instant) => instant is null ? this["Common.None"].Value : calendar.ToDate(instant.Value).ToString(DateFormat, CultureInfo.InvariantCulture);

    public string DateTime(DateTimeOffset? instant) => instant is null
        ? this["Common.None"].Value
        : calendar.ToLocal(instant.Value).ToString(DateTimeFormat, CultureInfo.InvariantCulture);

    public string Text(string? value) => string.IsNullOrWhiteSpace(value) ? this["Common.None"].Value : value;

    /// <summary>A byte count for people: B, KB, MB or GB with one decimal.</summary>
    public string FileSize(long bytes) => bytes switch
    {
        < 1024 => string.Create(CultureInfo.InvariantCulture, $"{bytes} B"),
        < 1024 * 1024 => string.Create(CultureInfo.InvariantCulture, $"{bytes / 1024.0:0.#} KB"),
        < 1024L * 1024 * 1024 => string.Create(CultureInfo.InvariantCulture, $"{bytes / (1024.0 * 1024):0.#} MB"),
        _ => string.Create(CultureInfo.InvariantCulture, $"{bytes / (1024.0 * 1024 * 1024):0.##} GB"),
    };

    // ------------------------------------------------------------- progress

    public string Progress(ProgressMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        return string.Format(CultureInfo.InvariantCulture, localizer[$"Progress.{message.Code}"].Value, [.. message.Arguments]);
    }

    /// <summary>The headline's visual weight: needs a decision, ready, finished, or ordinary waiting.</summary>
    public string ProgressModifier(ProgressMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        return message.Code switch
        {
            "P0" or "P1" => "headline--muted",
            "P1.Unresolved" or "P4" or "P5" or "P6" or "P6.NoOrganization" or "P7" => "headline--attention",
            "P16" or "P17" => "headline--ready",
            _ => string.Empty,
        };
    }

    public string Summary(ProgressSummary summary)
    {
        ArgumentNullException.ThrowIfNull(summary);
        var parts = new List<string>
        {
            Format("Progress.Summary.Branches", summary.Branches),
            Format("Progress.Summary.Responses", summary.RequestsConsidered, summary.RequestsAnswered),
            Format("Progress.Summary.OpenRequirements", summary.OpenRequirements),
        };

        if (summary.Overdue > 0)
        {
            parts.Add(Format("Progress.Summary.Overdue", summary.Overdue));
        }

        if (summary.Waived > 0)
        {
            parts.Add(Format("Progress.Summary.Waived", summary.Waived));
        }

        if (summary.Failed > 0)
        {
            parts.Add(Format("Progress.Summary.Failed", summary.Failed));
        }

        return string.Join(" · ", parts);
    }

    public string BranchState(BranchState state) => this[$"Branch.{state}"].Value;

    public string RequestProgress(RequestProgress derived)
    {
        ArgumentNullException.ThrowIfNull(derived);
        return derived.State == RequestProgressState.Overdue
            ? Format("RequestProgress.Overdue", derived.OverdueDays)
            : this[$"RequestProgress.{derived.State}"].Value;
    }

    public string RequestProgressBadge(RequestProgress derived)
    {
        ArgumentNullException.ThrowIfNull(derived);
        return derived.State switch
        {
            RequestProgressState.Overdue or RequestProgressState.Conflict => "badge--danger",
            RequestProgressState.AnsweredCloseable => "badge--ok",
            RequestProgressState.DueToday or RequestProgressState.AnsweredWithOpenBlocking => "badge--warn",
            RequestProgressState.AwaitingResponse or RequestProgressState.PartiallyAnswered => "badge--info",
            _ => string.Empty,
        };
    }

    public string FinalResultBadge(FinalResultStatus status) => status switch
    {
        FinalResultStatus.Issued => "badge--ok",
        FinalResultStatus.Draft => "badge--warn",
        FinalResultStatus.Revoked => "badge--danger",
        _ => "badge--plain",
    };

    public string RequirementProgress(RequirementProgress derived)
    {
        ArgumentNullException.ThrowIfNull(derived);
        return derived.OverdueDays > 0
            ? Format("RequirementProgress.Overdue", derived.OverdueDays)
            : this[$"RequirementProgress.{derived.State}"].Value;
    }

    public string RequirementProgressBadge(RequirementProgress derived)
    {
        ArgumentNullException.ThrowIfNull(derived);
        if (derived.OverdueDays > 0)
        {
            return "badge--danger";
        }

        return derived.State switch
        {
            RequirementProgressState.Fulfilled => "badge--ok",
            RequirementProgressState.Failed => "badge--danger",
            RequirementProgressState.Waived => "badge--warn",
            RequirementProgressState.OursToAct => "badge--warn",
            RequirementProgressState.WaitingExternal => "badge--info",
            _ => string.Empty,
        };
    }

    /// <summary>
    /// A status recorded in an audit payload, translated with the vocabulary of the entity it belongs to. Unknown
    /// combinations fall back to the raw code rather than hiding the fact.
    /// </summary>
    public string ActivityStatus(string entityType, string code)
    {
        var prefix = entityType switch
        {
            AuditEntityTypes.Case => "CaseState",
            AuditEntityTypes.Request => "RequestStatus",
            AuditEntityTypes.Requirement => "RequirementStatus",
            AuditEntityTypes.Response => "ResponseStatus",
            AuditEntityTypes.Correspondence => "CorrespondenceStatus",
            AuditEntityTypes.Assignment => "AssignmentStatus",
            AuditEntityTypes.User => "UserStatus",
            _ => null,
        };

        if (prefix is null)
        {
            return code;
        }

        var localized = localizer[$"{prefix}.{code}"];
        return localized.ResourceNotFound ? code : localized.Value;
    }

    // --------------------------------------------------------------- errors

    /// <summary>Translates a command failure by its stable code; unknown codes degrade to the code itself, never to a blank page.</summary>
    public string Error(CommandError error)
    {
        ArgumentNullException.ThrowIfNull(error);
        var localized = localizer[$"Error.{error.Code}"];
        return localized.ResourceNotFound ? Format("Error.Unknown", error.Code) : localized.Value;
    }
}
