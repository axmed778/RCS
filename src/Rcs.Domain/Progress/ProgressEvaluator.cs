using Rcs.Domain.Vocabulary;
using Rcs.Domain.Workflow;

namespace Rcs.Domain.Progress;

/// <summary>
/// Computes derived progress from a case snapshot (WORKFLOW.md §11). The headline precedence ladder is an ordered
/// rule list — data, not a branching function (§11.6) — evaluated first match wins, with the deterministic tie
/// breaks of §11.2(5).
/// </summary>
/// <remarks>
/// Implemented subset for the Review build: P0–P10 and P12–P18. Not implemented, because its input does not exist
/// yet: P11 (needs registered follow-up letters). Business dates (overdue days) are counted in the configured
/// business time zone.
/// </remarks>
public sealed class ProgressEvaluator(TimeZoneInfo businessTimeZone)
{
    private delegate ProgressMessage? Rule(Scope scope);

    // WORKFLOW.md §11.3 rows P4–P14 apply to a whole case and, restricted, to one branch (§11.5).
    private static readonly IReadOnlyList<Rule> OperationalLadder =
    [
        // P4 — an unresolved response conflict needs a decision.
        scope => scope.Requests.Where(scope.HasConflict).OrderBy(r => r.SentAt).ThenBy(r => r.Id.ToString("N"), StringComparer.Ordinal)
            .Select(r => ProgressMessage.Of("P4", r.TargetName)).FirstOrDefault(),

        // P5 — a blocking requirement FAILED and no result is issued: the decision still has to be taken.
        scope => scope.Snapshot.Case.Result.HasIssued
            ? null
            : scope.Requirements.Where(q => q.IsBlocking && q.Status == RequirementStatus.Failed)
                .OrderBy(q => q.RaisedAt).ThenBy(q => q.Id.ToString("N"), StringComparer.Ordinal)
                .Select(q => ProgressMessage.Of("P5", q.Title)).FirstOrDefault(),

        // P6 — a blocking requirement is open and overdue.
        scope => scope.Requirements.Where(q => q.IsBlocking && IsOpen(q) && scope.RequirementOverdueDays(q) > 0)
            .OrderByDescending(scope.RequirementOverdueDays).ThenBy(q => q.DueAt).ThenBy(q => q.RaisedAt).ThenBy(q => q.Id.ToString("N"), StringComparer.Ordinal)
            .Select(q => q.AddressedToName is null
                ? ProgressMessage.Of("P6.NoOrganization", q.Title, Days(scope.RequirementOverdueDays(q)))
                : ProgressMessage.Of("P6", q.Title, q.AddressedToName, Days(scope.RequirementOverdueDays(q))))
            .FirstOrDefault(),

        // P7 — a request is out, unanswered and overdue.
        scope => scope.Requests.Where(r => scope.RequestOverdueDays(r) > 0)
            .OrderByDescending(scope.RequestOverdueDays).ThenBy(r => r.DueAt).ThenBy(r => r.SentAt).ThenBy(r => r.Id.ToString("N"), StringComparer.Ordinal)
            .Select(r => ProgressMessage.Of("P7", r.TargetName, Days(scope.RequestOverdueDays(r)))).FirstOrDefault(),

        // P8 — a blocking requirement where the department owes the next move (branch state B2).
        scope => scope.Requirements.Where(q => q.IsBlocking && scope.OursToAct(q))
            .OrderBy(q => q.DueAt ?? DateTimeOffset.MaxValue).ThenBy(q => q.RaisedAt).ThenBy(q => q.Id.ToString("N"), StringComparer.Ordinal)
            .Select(q => ProgressMessage.Of("P8", q.Title)).FirstOrDefault(),

        // P9 — planned requests not yet officially issued.
        scope => scope.Requests.Count(r => r.Status == RequestStatus.Draft) is var drafts and > 0
            ? ProgressMessage.Of("P9", Count(drafts)) : null,

        // P10 — responses awaiting classification.
        scope => scope.Requests.SelectMany(r => r.Responses)
            .Count(p => p.Status == ResponseStatus.Active && p.OutcomeCode == ResponseOutcomeCodes.Undetermined) is var undetermined and > 0
            ? ProgressMessage.Of("P10", Count(undetermined)) : null,

        // P12 — a blocking requirement is waiting on a child request that is out.
        scope => scope.Requirements.Where(q => q.IsBlocking && IsOpen(q) && scope.SentChildren(q).Any())
            .OrderBy(q => q.DueAt ?? DateTimeOffset.MaxValue).ThenBy(q => q.RaisedAt).ThenBy(q => q.Id.ToString("N"), StringComparer.Ordinal)
            .Select(q => ProgressMessage.Of("P12", q.Title, q.AddressedToName ?? scope.SentChildren(q).First().TargetName))
            .FirstOrDefault(),

        // P13 / P14 — waiting for authority responses.
        scope => scope.Requests.Where(scope.IsUnanswered).OrderBy(r => r.SentAt).ThenBy(r => r.Id.ToString("N"), StringComparer.Ordinal).ToArray() switch
        {
            [var only] => ProgressMessage.Of("P13", only.TargetName),
            { Length: > 1 } many => ProgressMessage.Of("P14", Count(many.Length)),
            _ => null,
        },

        // P15 — the decision is drafted and the Head has not approved it yet (§8.4).
        scope => scope.Snapshot.Case.Result.HasDraftAwaitingApproval ? ProgressMessage.Of("P15") : null,
    ];

    public CaseProgress Evaluate(ProgressSnapshot snapshot, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var today = BusinessDate(now);
        var caseScope = new Scope(snapshot.Requests, snapshot.Requirements, snapshot, now, today, this);

        var requestProgress = snapshot.Requests.ToDictionary(r => r.Id, r => DeriveRequest(caseScope, r));
        var requirementProgress = snapshot.Requirements.ToDictionary(q => q.Id, q => DeriveRequirement(caseScope, q));
        var branches = snapshot.Requests
            .Where(r => r.SourceRequirementId is null)
            .ToDictionary(r => r.Id, r => DeriveBranch(caseScope, r));

        var ready = IsReadyForFinalResult(caseScope);
        var unresolved = UnresolvedAtClosure(caseScope);
        var headline = CaseHeadline(caseScope, ready, unresolved);

        var summary = new ProgressSummary(
            Branches: snapshot.Requests.Count(r => r.SourceRequirementId is null && r.Status != RequestStatus.Void),
            RequestsConsidered: snapshot.Requests.Count(r => r.Status is not (RequestStatus.Void or RequestStatus.Withdrawn)),
            RequestsAnswered: snapshot.Requests.Count(r => r.Status is not (RequestStatus.Void or RequestStatus.Withdrawn)
                && RequestRules.HasActiveConclusive(Facts(r))),
            OpenRequirements: snapshot.Requirements.Count(IsOpen),
            Overdue: snapshot.Requests.Count(r => caseScope.RequestOverdueDays(r) > 0) + snapshot.Requirements.Count(q => caseScope.RequirementOverdueDays(q) > 0),
            Waived: snapshot.Requirements.Count(q => q.Status == RequirementStatus.Waived),
            Failed: snapshot.Requirements.Count(q => q.Status == RequirementStatus.Failed));

        return new CaseProgress(
            headline,
            summary,
            ready,
            branches,
            requestProgress,
            requirementProgress,
            ClosedWithUnresolvedItems: snapshot.Case.State == CaseLifecycleState.Closed && unresolved > 0,
            UnresolvedAtClosure: snapshot.Case.State == CaseLifecycleState.Closed ? unresolved : 0);
    }

    /// <summary>
    /// Items still open on the case: non-blocking requirements left at a normal closure (WORKFLOW.md §9.2.1) and
    /// anything a Head override left behind (§9.4). Counted the same way in both cases, because the record is the
    /// same — nothing was auto-resolved to make the closure pass.
    /// </summary>
    private static int UnresolvedAtClosure(Scope scope) =>
        scope.Requirements.Count(q => !q.Status.IsTerminal()) + scope.Requests.Count(r => !r.Status.IsTerminal());

    public DateOnly BusinessDate(DateTimeOffset instant) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(instant, businessTimeZone).DateTime);

    private static ProgressMessage CaseHeadline(Scope scope, bool ready, int unresolved)
    {
        var facts = scope.Snapshot.Case;
        switch (facts.State)
        {
            case CaseLifecycleState.Cancelled:
                return ProgressMessage.Of("P0");                                               // P0

            // P1 — when the closure left work open, the headline says so and how much (§9.2.1 obligation 3).
            // What the closure produced is shown by the case's own final-result panel, where its decision type
            // can be translated; a headline argument would print a raw database code.
            case CaseLifecycleState.Closed:
                var closedOn = facts.ClosedAt is { } closedAt
                    ? scope.Evaluator.BusinessDate(closedAt).ToString("dd.MM.yyyy", System.Globalization.CultureInfo.InvariantCulture)
                    : string.Empty;
                return unresolved > 0
                    ? ProgressMessage.Of("P1.Unresolved", closedOn, Count(unresolved))
                    : ProgressMessage.Of("P1", closedOn);
            case CaseLifecycleState.OnHold:
                return facts.HoldUntil is { } until                                            // P2
                    ? ProgressMessage.Of("P2", until.ToString("dd.MM.yyyy", System.Globalization.CultureInfo.InvariantCulture), facts.HoldReason ?? string.Empty)
                    : ProgressMessage.Of("P2.Open", facts.HoldReason ?? string.Empty);
            case CaseLifecycleState.Registered when scope.Requests.Count == 0:
                return ProgressMessage.Of("P3");                                               // P3
        }

        foreach (var rule in OperationalLadder)
        {
            if (rule(scope) is { } message)
            {
                return message;
            }
        }

        // P17 — the decision is out; what remains is to close the case (§9.3: never automatic).
        if (scope.Snapshot.Case.Result is { HasIssued: true, IssuedAt: var issuedAt })
        {
            return ProgressMessage.Of("P17", issuedAt is { } at
                ? scope.Evaluator.BusinessDate(at).ToString("dd.MM.yyyy", System.Globalization.CultureInfo.InvariantCulture)
                : string.Empty);
        }

        return ready ? ProgressMessage.Of("P16") : ProgressMessage.Of("P18");                  // P16, P18
    }

    /// <summary>
    /// WORKFLOW.md §7.4: every request terminal (3), no blocking requirement open (2), no unresolved conflict (4)
    /// and no result already issued (5). Condition 1 (every branch terminal) is implied by 2 and 3 for the states
    /// this build produces. Condition 5's exception — an issued result never blocks its own replacement (§8.6) —
    /// belongs to the caller, which decides whether the result in force counts against the draft being issued.
    /// </summary>
    private static bool IsReadyForFinalResult(Scope scope) =>
        scope.Snapshot.Case.State is CaseLifecycleState.Active or CaseLifecycleState.Registered
        && scope.Requests.Count > 0
        && scope.Requests.All(r => r.Status.IsTerminal())
        && !scope.Requirements.Any(q => q.IsBlocking && IsOpen(q))
        && !scope.Requests.Any(scope.HasConflict)
        && !scope.Snapshot.Case.Result.HasIssued;

    private BranchProgress DeriveBranch(Scope caseScope, ProgressRequestFacts topLevel)
    {
        var requests = new List<ProgressRequestFacts> { topLevel };
        var requirements = new List<ProgressRequirementFacts>();
        var frontier = new Queue<ProgressRequestFacts>([topLevel]);
        while (frontier.TryDequeue(out var request))
        {
            foreach (var requirement in caseScope.Snapshot.Requirements.Where(q => q.SourceRequestId == request.Id))
            {
                requirements.Add(requirement);
                foreach (var child in caseScope.Snapshot.Requests.Where(r => r.SourceRequirementId == requirement.Id))
                {
                    requests.Add(child);
                    frontier.Enqueue(child);
                }
            }
        }

        var scope = new Scope(requests, requirements, caseScope.Snapshot, caseScope.Now, caseScope.Today, this);

        var state =
            requirements.Any(q => q.IsBlocking && q.Status == RequirementStatus.Failed) ? BranchState.Failed
            : requirements.Any(q => q.IsBlocking && scope.OursToAct(q)) ? BranchState.Blocked
            : requests.Any(scope.IsUnanswered) ? BranchState.WaitingExternal
            : requests.Any(r => r.Status == RequestStatus.Draft)
              || requests.SelectMany(r => r.Responses).Any(p => p.Status == ResponseStatus.Active && p.OutcomeCode == ResponseOutcomeCodes.Undetermined)
                ? BranchState.WaitingInternal
            : topLevel.Status is RequestStatus.Withdrawn or RequestStatus.Void ? BranchState.Withdrawn
            : topLevel.Status == RequestStatus.Closed && !requirements.Any(q => q.IsBlocking && IsOpen(q)) ? BranchState.Complete
            : BranchState.Answered;

        ProgressMessage? headline = null;
        foreach (var rule in OperationalLadder)
        {
            if (rule(scope) is { } message)
            {
                headline = message;
                break;
            }
        }

        return new BranchProgress(topLevel.Id, state, headline);
    }

    private static RequestProgress DeriveRequest(Scope scope, ProgressRequestFacts request)
    {
        var responses = Facts(request);
        var overdue = scope.RequestOverdueDays(request);
        var state = request.Status switch
        {
            RequestStatus.Void => RequestProgressState.Void,
            RequestStatus.Withdrawn => RequestProgressState.Withdrawn,
            RequestStatus.Draft => RequestProgressState.Draft,
            RequestStatus.Closed => RequestProgressState.Closed,
            _ when scope.HasConflict(request) => RequestProgressState.Conflict,
            RequestStatus.Sent when overdue > 0 => RequestProgressState.Overdue,
            RequestStatus.Sent when scope.IsDueToday(request) => RequestProgressState.DueToday,
            RequestStatus.Sent when request.Responses.Any(p => p.Status == ResponseStatus.Active) => RequestProgressState.PartiallyAnswered,
            RequestStatus.Sent => RequestProgressState.AwaitingResponse,
            _ => RequestRules.CanClose(request.Status, responses, RaisedBy(scope, request)).IsAllowed
                ? RequestProgressState.AnsweredCloseable
                : RequestProgressState.AnsweredWithOpenBlocking,
        };

        return new RequestProgress(state, overdue);
    }

    private static RequirementProgress DeriveRequirement(Scope scope, ProgressRequirementFacts requirement)
    {
        var state = requirement.Status switch
        {
            RequirementStatus.Fulfilled => RequirementProgressState.Fulfilled,
            RequirementStatus.Waived => RequirementProgressState.Waived,
            RequirementStatus.Void => RequirementProgressState.Void,
            RequirementStatus.Failed => RequirementProgressState.Failed,
            _ => scope.SentChildren(requirement).Any() ? RequirementProgressState.WaitingExternal : RequirementProgressState.OursToAct,
        };

        return new RequirementProgress(state, scope.RequirementOverdueDays(requirement));
    }

    private static IReadOnlyCollection<ResponseFacts> Facts(ProgressRequestFacts request) =>
        request.Responses.Select(p => new ResponseFacts(p.Status, p.IsConclusive, p.OutcomeCode)).ToArray();

    private static IEnumerable<RequirementFacts> RaisedBy(Scope scope, ProgressRequestFacts request) =>
        scope.Snapshot.Requirements.Where(q => q.SourceRequestId == request.Id).Select(q => new RequirementFacts(q.Status, q.IsBlocking));

    private static bool IsOpen(ProgressRequirementFacts requirement) =>
        requirement.Status is RequirementStatus.Open or RequirementStatus.InProgress;

    private static string Count(int value) => value.ToString(System.Globalization.CultureInfo.InvariantCulture);

    private static string Days(int value) => Count(value);

    /// <summary>A set of requests and requirements (a whole case, or one branch) with the derivations the ladder reads.</summary>
    private sealed class Scope(
        IReadOnlyList<ProgressRequestFacts> requests,
        IReadOnlyList<ProgressRequirementFacts> requirements,
        ProgressSnapshot snapshot,
        DateTimeOffset now,
        DateOnly today,
        ProgressEvaluator evaluator)
    {
        public IReadOnlyList<ProgressRequestFacts> Requests { get; } = requests;

        public IReadOnlyList<ProgressRequirementFacts> Requirements { get; } = requirements;

        public ProgressSnapshot Snapshot { get; } = snapshot;

        public DateTimeOffset Now { get; } = now;

        public DateOnly Today { get; } = today;

        public ProgressEvaluator Evaluator { get; } = evaluator;

        public bool HasConflict(ProgressRequestFacts request) =>
            request.Status is not (RequestStatus.Void or RequestStatus.Withdrawn) && RequestRules.HasConflict(Facts(request));

        /// <summary>SENT with no ACTIVE conclusive response.</summary>
        public bool IsUnanswered(ProgressRequestFacts request) =>
            request.Status == RequestStatus.Sent && !RequestRules.HasActiveConclusive(Facts(request));

        /// <summary>WORKFLOW.md §12.2: overdue only while SENT and unanswered; never stored.</summary>
        public int RequestOverdueDays(ProgressRequestFacts request) =>
            IsUnanswered(request) && request.DueAt is { } due && due < Now ? OverdueDays(due) : 0;

        /// <summary>
        /// Out, unanswered, and the deadline falls on today's business date — not yet overdue, because a deadline
        /// entered as a date means "by the end of that day" (WORKFLOW.md §12.2).
        /// </summary>
        public bool IsDueToday(ProgressRequestFacts request) =>
            IsUnanswered(request) && request.DueAt is { } due && Evaluator.BusinessDate(due) == Today;

        public int RequirementOverdueDays(ProgressRequirementFacts requirement) =>
            IsOpen(requirement) && requirement.DueAt is { } due && due < Now ? OverdueDays(due) : 0;

        /// <summary>Child requests of the requirement that are out and unanswered (the whole case is searched).</summary>
        public IEnumerable<ProgressRequestFacts> SentChildren(ProgressRequirementFacts requirement) =>
            Snapshot.Requests.Where(r => r.SourceRequirementId == requirement.Id && r.Status == RequestStatus.Sent);

        /// <summary>Branch state B2: open, no child request out, no ACTIVE evidence — the department owes the next move.</summary>
        public bool OursToAct(ProgressRequirementFacts requirement) =>
            IsOpen(requirement) && !SentChildren(requirement).Any() && requirement.ActiveEvidenceCount == 0;

        private int OverdueDays(DateTimeOffset due) =>
            Math.Max(1, Today.DayNumber - Evaluator.BusinessDate(due).DayNumber);
    }
}
