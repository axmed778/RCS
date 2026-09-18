using Rcs.Domain.Vocabulary;
using Rcs.Domain.Workflow;

namespace Rcs.UnitTests.Domain;

/// <summary>
/// The rules that end a case, as pure functions: the department's deadline rule (ADR-041), the closure guards
/// G1–G5 (WORKFLOW.md §9.2) and the final-result transitions F1–F4 (§8).
/// </summary>
public sealed class LifecycleRuleTests
{
    // ------------------------------------------------------------------ deadlines

    [Fact]
    public void TheDeadlineIsTenCalendarDaysFromTheSentDate() =>
        Assert.Equal(new DateOnly(2026, 9, 28), RequestDeadline.Suggest(new DateOnly(2026, 9, 18)));

    /// <summary>
    /// Weekends and holidays count: there is no working-day calculation and no holiday table (ADR-041). Friday
    /// 18 September 2026 + 10 days is Monday 28 September, not the twelfth working day.
    /// </summary>
    [Theory]
    [InlineData("2026-09-18", "2026-09-28")]   // Friday → Monday, two weekends' worth of days included
    [InlineData("2026-12-26", "2027-01-05")]   // across a year boundary
    [InlineData("2028-02-25", "2028-03-06")]   // across a leap day
    public void TheDeadlineCountsEveryCalendarDay(string sent, string due) =>
        Assert.Equal(DateOnly.Parse(due, System.Globalization.CultureInfo.InvariantCulture),
            RequestDeadline.Suggest(DateOnly.Parse(sent, System.Globalization.CultureInfo.InvariantCulture)));

    [Fact]
    public void ADeadlineIsRecognisedAsTheDepartmentsDefaultOnlyWhenItIsExactlyThat()
    {
        var sent = new DateOnly(2026, 9, 18);

        Assert.True(RequestDeadline.IsDefaultFor(sent, new DateOnly(2026, 9, 28)));
        Assert.False(RequestDeadline.IsDefaultFor(sent, new DateOnly(2026, 9, 29)));
    }

    // ------------------------------------------------------------ closure guards

    [Fact]
    public void EveryGuardPassesOnAFinishedCase() =>
        Assert.All(CaseClosureRules.Evaluate(Facts()), guard => Assert.True(guard.Passed, guard.Code));

    /// <summary>G1 counts blocking work only. A non-blocking requirement holds nothing open — that is what the flag is for.</summary>
    [Fact]
    public void AnOpenNonBlockingRequirementDoesNotFailAnyGuard()
    {
        var guards = CaseClosureRules.Evaluate(Facts(requirements: [new RequirementFacts(RequirementStatus.Open, IsBlocking: false)]));

        Assert.All(guards, guard => Assert.True(guard.Passed, guard.Code));
    }

    [Fact]
    public void AnOpenBlockingRequirementFailsG1()
    {
        var guards = CaseClosureRules.Evaluate(Facts(requirements: [new RequirementFacts(RequirementStatus.InProgress, IsBlocking: true)]));

        Assert.Equal(["G1"], Failing(guards));
        Assert.True(guards.Single(guard => guard.Code == "G1").IsOverridable);
    }

    /// <summary>
    /// Requests are never exempt: a letter sent to satisfy a non-blocking requirement is still an open letter to an
    /// authority, and G2 still requires it to be terminal.
    /// </summary>
    [Fact]
    public void AnUnansweredRequestFailsG2()
    {
        var guards = CaseClosureRules.Evaluate(Facts(requests: [new ClosureRequestFacts(RequestStatus.Sent)]));

        Assert.Equal(["G2"], Failing(guards));
    }

    [Fact]
    public void G3AppliesOnlyToClosureTypesThatProduceADecision()
    {
        Assert.Equal(["G3"], Failing(CaseClosureRules.Evaluate(Facts(hasIssuedResult: false, producesDecision: true))));
        Assert.Empty(Failing(CaseClosureRules.Evaluate(Facts(hasIssuedResult: false, producesDecision: false))));
    }

    [Fact]
    public void ACaseNobodyEverOwnedFailsG5() =>
        Assert.Equal(["G5"], Failing(CaseClosureRules.Evaluate(Facts(hasResponsible: false))));

    [Fact]
    public void AnAlreadyClosedCaseIsNotAnOverrideCaseButAMistake()
    {
        Assert.Equal("case.already_closed", CaseClosureRules.CanAttemptClosure(CaseLifecycleState.Closed).ViolationCode);
        Assert.Equal("case.already_closed", CaseClosureRules.CanAttemptClosure(CaseLifecycleState.Cancelled).ViolationCode);
        Assert.True(CaseClosureRules.CanAttemptClosure(CaseLifecycleState.OnHold).IsAllowed);
    }

    [Fact]
    public void OnlyUnresolvedNonBlockingRequirementsAreListedForTheClosureScreen()
    {
        RequirementFacts[] requirements =
        [
            new(RequirementStatus.Open, IsBlocking: false),
            new(RequirementStatus.InProgress, IsBlocking: false),
            new(RequirementStatus.Fulfilled, IsBlocking: false),
            new(RequirementStatus.Open, IsBlocking: true),
        ];

        Assert.Equal(2, CaseClosureRules.UnresolvedNonBlocking(requirements, requirement => requirement).Count());
    }

    [Fact]
    public void ReopeningNeedsAClosedCaseAndAReason()
    {
        Assert.Equal("case.not_closed", CaseClosureRules.CanReopen(CaseLifecycleState.Active, "səbəb").ViolationCode);
        Assert.Equal("case.reopen_reason_required", CaseClosureRules.CanReopen(CaseLifecycleState.Closed, "   ").ViolationCode);
        Assert.True(CaseClosureRules.CanReopen(CaseLifecycleState.Closed, "Gec gələn məktub").IsAllowed);
    }

    // -------------------------------------------------------------- final result

    [Fact]
    public void ADraftNeedsASummaryAndACaseStillOpenToWork()
    {
        Assert.Equal("case.not_open_to_work", FinalResultRules.CanDraft(CaseLifecycleState.Closed, "mətn").ViolationCode);
        Assert.Equal("final_result.summary_required", FinalResultRules.CanDraft(CaseLifecycleState.Active, " ").ViolationCode);
        Assert.True(FinalResultRules.CanDraft(CaseLifecycleState.Active, "mətn").IsAllowed);
    }

    /// <summary>D1 is overridable with a mandatory reason; only a DRAFT can be issued at all.</summary>
    [Fact]
    public void IssuingOverAnUnreadyCaseNeedsAReasonAndOnlyADraftCanBeIssued()
    {
        Assert.True(FinalResultRules.CanIssue(FinalResultStatus.Draft, isReadyForFinalResult: true, null, 1, null).IsAllowed);
        Assert.Equal("final_result.case_not_ready", FinalResultRules.CanIssue(FinalResultStatus.Draft, false, "  ", 1, null).ViolationCode);
        Assert.True(FinalResultRules.CanIssue(FinalResultStatus.Draft, false, "Orqan cavab verməyəcək", 1, null).IsAllowed);
        Assert.Equal("final_result.not_draft", FinalResultRules.CanIssue(FinalResultStatus.Issued, true, null, 1, null).ViolationCode);
    }

    /// <summary>D4: the signed decision must be on file, unless the Head records why it will follow.</summary>
    [Fact]
    public void IssuingWithoutADecisionDocumentNeedsTheHeadsReason()
    {
        Assert.Equal("final_result.document_required", FinalResultRules.CanIssue(FinalResultStatus.Draft, true, null, 0, null).ViolationCode);
        Assert.Equal("final_result.document_required", FinalResultRules.CanIssue(FinalResultStatus.Draft, true, null, 0, " ").ViolationCode);
        Assert.True(FinalResultRules.CanIssue(FinalResultStatus.Draft, true, null, 0, "İmzalanmış qərar sabah skan ediləcək").IsAllowed);
        Assert.True(FinalResultRules.CanIssue(FinalResultStatus.Draft, true, null, 2, null).IsAllowed);
    }

    [Fact]
    public void OnlyAnIssuedResultIsRevokedAndOnlyWithAReason()
    {
        Assert.Equal("final_result.not_issued", FinalResultRules.CanRevoke(FinalResultStatus.Draft, "səbəb").ViolationCode);
        Assert.Equal("final_result.revocation_reason_required", FinalResultRules.CanRevoke(FinalResultStatus.Issued, null).ViolationCode);
        Assert.True(FinalResultRules.CanRevoke(FinalResultStatus.Issued, "Məhkəmə qərarı").IsAllowed);
    }

    // ------------------------------------------------------------------ fixtures

    private static ClosureFacts Facts(
        IReadOnlyCollection<ClosureRequestFacts>? requests = null,
        IReadOnlyCollection<RequirementFacts>? requirements = null,
        bool hasIssuedResult = true,
        bool producesDecision = true,
        bool hasResponsible = true) =>
        new(CaseLifecycleState.Active,
            requests ?? [new ClosureRequestFacts(RequestStatus.Closed)],
            requirements ?? [new RequirementFacts(RequirementStatus.Fulfilled, IsBlocking: true)],
            hasIssuedResult,
            producesDecision,
            hasResponsible);

    private static string[] Failing(IEnumerable<ClosureGuard> guards) =>
        guards.Where(guard => !guard.Passed).Select(guard => guard.Code).ToArray();
}
