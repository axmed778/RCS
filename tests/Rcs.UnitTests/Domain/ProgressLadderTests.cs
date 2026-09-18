using Rcs.Domain.Progress;
using Rcs.Domain.Vocabulary;

namespace Rcs.UnitTests.Domain;

/// <summary>
/// The derived-progress precedence ladder of WORKFLOW.md §11.3: first match wins, exceptions outrank routine waiting,
/// and overdue external work outranks our own routine work. Nothing here is ever stored (ADR-029).
/// </summary>
public sealed class ProgressLadderTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 18, 9, 0, 0, TimeSpan.Zero);
    private static readonly ProgressEvaluator Evaluator = new(TimeZoneInfo.Utc);

    private static Guid Id(int seed) => new($"01995c20-0000-7000-8000-{seed:D12}");

    private static ProgressRequestFacts Request(
        int id,
        RequestStatus status = RequestStatus.Sent,
        string target = "Memarlıq Orqanı",
        int? dueInDays = null,
        Guid? sourceRequirementId = null,
        params ProgressResponseFacts[] responses) =>
        new(Id(id), status, target, sourceRequirementId, Now.AddDays(-20),
            dueInDays is { } days ? Now.AddDays(days) : null, responses);

    private static ProgressResponseFacts Response(int id, bool conclusive, string outcome = "APPROVED", ResponseStatus status = ResponseStatus.Active) =>
        new(Id(id), status, conclusive, outcome);

    private static ProgressRequirementFacts Requirement(
        int id,
        RequirementStatus status = RequirementStatus.Open,
        bool blocking = true,
        string title = "Kommunikasiya xəritəsi",
        int? dueInDays = null,
        Guid? sourceRequestId = null,
        string? addressedTo = "Kommunal Orqan",
        int evidence = 0) =>
        new(Id(id), status, blocking, title, addressedTo, "Memarlıq Orqanı", sourceRequestId, Now.AddDays(-15),
            dueInDays is { } days ? Now.AddDays(days) : null, evidence);

    private static CaseProgress Evaluate(
        CaseLifecycleState state,
        IReadOnlyList<ProgressRequestFacts> requests,
        IReadOnlyList<ProgressRequirementFacts>? requirements = null,
        DateOnly? holdUntil = null,
        string? holdReason = null) =>
        Evaluator.Evaluate(
            new ProgressSnapshot(new ProgressCaseFacts(state, state == CaseLifecycleState.Closed ? Now : null, holdUntil, holdReason), requests, requirements ?? []),
            Now);

    [Fact]
    public void ARegisteredCaseWithNoRequestSaysSo() =>
        Assert.Equal("P3", Evaluate(CaseLifecycleState.Registered, []).Headline.Code);

    [Fact]
    public void ACancelledOrClosedCaseSaysOnlyThat()
    {
        Assert.Equal("P0", Evaluate(CaseLifecycleState.Cancelled, [Request(1)]).Headline.Code);
        Assert.Equal("P1", Evaluate(CaseLifecycleState.Closed, [Request(1, RequestStatus.Closed)]).Headline.Code);
    }

    /// <summary>
    /// WORKFLOW.md §9.2.1 obligation 3: a case closed with work still open says so, and says how much. Nothing was
    /// auto-resolved to make the closure pass, which is exactly why the count can be derived at all.
    /// </summary>
    [Fact]
    public void AClosedCaseWithOpenWorkSaysWhatWasLeft()
    {
        var progress = Evaluate(
            CaseLifecycleState.Closed,
            [Request(1, RequestStatus.Closed)],
            [Requirement(2, RequirementStatus.Open, blocking: false)]);

        Assert.Equal("P1.Unresolved", progress.Headline.Code);
        Assert.Equal("1", progress.Headline.Arguments[1]);
        Assert.True(progress.ClosedWithUnresolvedItems);
        Assert.Equal(1, progress.UnresolvedAtClosure);
    }

    [Fact]
    public void AnOpenCaseIsNeverReportedAsClosedWithUnresolvedItems()
    {
        var progress = Evaluate(CaseLifecycleState.Active, [Request(1)], [Requirement(2, RequirementStatus.Open, blocking: false)]);

        Assert.False(progress.ClosedWithUnresolvedItems);
        Assert.Equal(0, progress.UnresolvedAtClosure);
    }

    [Fact]
    public void AHoldOutranksOperationalDetail()
    {
        var progress = Evaluate(CaseLifecycleState.OnHold, [Request(1, dueInDays: -30)], holdUntil: new DateOnly(2026, 10, 1), holdReason: "Müraciət edən məlumat təqdim etməlidir");

        Assert.Equal("P2", progress.Headline.Code);
        Assert.Equal("01.10.2026", progress.Headline.Arguments[0]);
    }

    [Fact]
    public void WaitingForOneAuthorityNamesIt()
    {
        var progress = Evaluate(CaseLifecycleState.Active, [Request(1, target: "Əmlak Orqanı")]);

        Assert.Equal("P13", progress.Headline.Code);
        Assert.Equal("Əmlak Orqanı", progress.Headline.Arguments[0]);
    }

    [Fact]
    public void WaitingForSeveralAuthoritiesCountsThem()
    {
        var progress = Evaluate(CaseLifecycleState.Active, [Request(1), Request(2, target: "FH Orqanı"), Request(3, target: "Əmlak Orqanı")]);

        Assert.Equal("P14", progress.Headline.Code);
        Assert.Equal("3", progress.Headline.Arguments[0]);
    }

    [Fact]
    public void AnOverdueAuthorityOutranksRoutineWaiting()
    {
        var progress = Evaluate(CaseLifecycleState.Active,
        [
            Request(1, target: "Memarlıq Orqanı", dueInDays: -6),
            Request(2, target: "Əmlak Orqanı"),
        ]);

        Assert.Equal("P7", progress.Headline.Code);
        Assert.Equal("Memarlıq Orqanı", progress.Headline.Arguments[0]);
        Assert.Equal("6", progress.Headline.Arguments[1]);
        Assert.Equal(1, progress.Summary.Overdue);
    }

    [Fact]
    public void AConflictOutranksEverythingOperational()
    {
        var conflicting = Request(1, RequestStatus.Answered, "Memarlıq Orqanı", dueInDays: -10,
            responses: [Response(10, conclusive: true, "APPROVED"), Response(11, conclusive: true, "REJECTED")]);

        var progress = Evaluate(CaseLifecycleState.Active, [conflicting]);

        Assert.Equal("P4", progress.Headline.Code);
        Assert.Equal(RequestProgressState.Conflict, progress.Requests[conflicting.Id].State);
    }

    [Fact]
    public void AFailedBlockingRequirementAsksForADecision()
    {
        var request = Request(1, RequestStatus.Answered, responses: [Response(10, conclusive: true)]);
        var progress = Evaluate(CaseLifecycleState.Active, [request], [Requirement(2, RequirementStatus.Failed, sourceRequestId: request.Id)]);

        Assert.Equal("P5", progress.Headline.Code);
        Assert.Equal(1, progress.Summary.Failed);
    }

    [Fact]
    public void AnOpenRequirementWithNoChildRequestIsOurMoveAndThenTheirs()
    {
        var request = Request(1, RequestStatus.Sent, responses: [Response(10, conclusive: false, "CONDITIONAL")]);
        var requirement = Requirement(2, sourceRequestId: request.Id);

        var ours = Evaluate(CaseLifecycleState.Active, [request], [requirement]);
        Assert.Equal("P8", ours.Headline.Code);                                            // branch state B2
        Assert.Equal(RequirementProgressState.OursToAct, ours.Requirements[requirement.Id].State);
        Assert.Equal(BranchState.Blocked, ours.Branches[request.Id].State);

        var child = Request(3, RequestStatus.Sent, "Kommunal Orqan", sourceRequirementId: requirement.Id);
        var theirs = Evaluate(CaseLifecycleState.Active, [request, child], [requirement with { Status = RequirementStatus.InProgress }]);
        Assert.Equal("P12", theirs.Headline.Code);                                          // waiting on the child request
        Assert.Equal("Kommunikasiya xəritəsi", theirs.Headline.Arguments[0]);
        Assert.Equal(BranchState.WaitingExternal, theirs.Branches[request.Id].State);
    }

    [Fact]
    public void AnOverdueRequirementOutranksAnOverdueRequest()
    {
        var request = Request(1, RequestStatus.Sent, dueInDays: -3, responses: [Response(10, conclusive: false, "CONDITIONAL")]);
        var requirement = Requirement(2, dueInDays: -9, sourceRequestId: request.Id);

        var progress = Evaluate(CaseLifecycleState.Active, [request], [requirement]);

        Assert.Equal("P6", progress.Headline.Code);
        Assert.Equal("Kommunikasiya xəritəsi", progress.Headline.Arguments[0]);
        Assert.Equal("Kommunal Orqan", progress.Headline.Arguments[1]);
        Assert.Equal("9", progress.Headline.Arguments[2]);
    }

    [Fact]
    public void ResponsesAwaitingClassificationAreSurfaced()
    {
        var request = Request(1, RequestStatus.Sent, responses: [Response(10, conclusive: false, ResponseOutcomeCodes.Undetermined)]);

        // Ranked below overdue external work but above ordinary waiting.
        Assert.Equal("P10", Evaluate(CaseLifecycleState.Active, [request]).Headline.Code);
    }

    [Fact]
    public void EverythingTerminalIsReadyForTheFinalResult()
    {
        var request = Request(1, RequestStatus.Closed, responses: [Response(10, conclusive: true)]);
        var requirement = Requirement(2, RequirementStatus.Fulfilled, sourceRequestId: request.Id, evidence: 1);

        var progress = Evaluate(CaseLifecycleState.Active, [request], [requirement]);

        Assert.Equal("P16", progress.Headline.Code);
        Assert.True(progress.IsReadyForFinalResult);
        Assert.Equal(BranchState.Complete, progress.Branches[request.Id].State);
    }

    [Fact]
    public void TheSummaryCountsBranchesResponsesAndOpenRequirements()
    {
        var first = Request(1, RequestStatus.Answered, responses: [Response(10, conclusive: true)]);
        var second = Request(2, RequestStatus.Answered, "FH Orqanı", responses: [Response(11, conclusive: true)]);
        var third = Request(3, RequestStatus.Sent, "Əmlak Orqanı");
        var requirement = Requirement(4, sourceRequestId: first.Id);

        var summary = Evaluate(CaseLifecycleState.Active, [first, second, third], [requirement]).Summary;

        Assert.Equal(3, summary.Branches);
        Assert.Equal(3, summary.RequestsConsidered);
        Assert.Equal(2, summary.RequestsAnswered);
        Assert.Equal(1, summary.OpenRequirements);
    }

    [Fact]
    public void ANonBlockingRequirementHoldsNothingOpen()
    {
        var request = Request(1, RequestStatus.Closed, responses: [Response(10, conclusive: true)]);
        var nonBlocking = Requirement(2, blocking: false, sourceRequestId: request.Id);

        var progress = Evaluate(CaseLifecycleState.Active, [request], [nonBlocking]);

        Assert.Equal("P16", progress.Headline.Code);                 // readiness ignores it (ADR-012)
        Assert.True(progress.IsReadyForFinalResult);
        Assert.Equal(1, progress.Summary.OpenRequirements);          // but it stays visible
        Assert.Equal(BranchState.Complete, progress.Branches[request.Id].State);
    }
}
