using Rcs.Domain.Vocabulary;
using Rcs.Domain.Workflow;

namespace Rcs.UnitTests.Domain;

/// <summary>The request and requirement guards of WORKFLOW.md §3 and §5, as rules rather than as screen behaviour.</summary>
public sealed class WorkflowRuleTests
{
    private static ResponseFacts Conclusive(string outcome = "APPROVED") => new(ResponseStatus.Active, true, outcome);

    private static ResponseFacts NotConclusive(string outcome = "NOT_APPLICABLE") => new(ResponseStatus.Active, false, outcome);

    [Fact]
    public void AConclusiveResponseAnswersASentRequest() =>
        Assert.Equal(RequestStatus.Answered, RequestRules.Consequence(RequestStatus.Sent, [Conclusive()], []));

    [Fact]
    public void AnAcknowledgementLeavesTheRequestSent() =>
        Assert.Equal(RequestStatus.Sent, RequestRules.Consequence(RequestStatus.Sent, [NotConclusive()], []));

    [Fact]
    public void LosingTheConclusiveAnswerReturnsTheRequestToSent()
    {
        // R7: the conclusive response was superseded by a non-conclusive one, or voided.
        var superseded = new ResponseFacts(ResponseStatus.Superseded, true, "APPROVED");

        Assert.Equal(RequestStatus.Sent, RequestRules.Consequence(RequestStatus.Answered, [superseded, NotConclusive()], []));
        Assert.Equal(RequestStatus.Sent, RequestRules.Consequence(RequestStatus.Closed, [superseded], []));       // R8
    }

    [Fact]
    public void NewBlockingWorkReopensAClosedRequestToAnswered()
    {
        // R9: still answered, no longer closeable.
        var next = RequestRules.Consequence(RequestStatus.Closed, [Conclusive()], [new RequirementFacts(RequirementStatus.Open, IsBlocking: true)]);

        Assert.Equal(RequestStatus.Answered, next);
    }

    [Fact]
    public void ANonBlockingRequirementDoesNotReopenAClosedRequest() =>
        Assert.Equal(RequestStatus.Closed, RequestRules.Consequence(RequestStatus.Closed, [Conclusive()], [new RequirementFacts(RequirementStatus.Open, IsBlocking: false)]));

    [Fact]
    public void ClosureNeedsAConclusiveAnswerAndEveryBlockingRequirementTerminal()
    {
        Assert.Equal("request.not_answered", RequestRules.CanClose(RequestStatus.Sent, [Conclusive()], []).ViolationCode);
        Assert.Equal("request.no_conclusive_response", RequestRules.CanClose(RequestStatus.Answered, [NotConclusive()], []).ViolationCode);
        Assert.Equal("request.conflicting_responses", RequestRules.CanClose(RequestStatus.Answered, [Conclusive("APPROVED"), Conclusive("REJECTED")], []).ViolationCode);
        Assert.Equal("request.blocking_requirements_open",
            RequestRules.CanClose(RequestStatus.Answered, [Conclusive()], [new RequirementFacts(RequirementStatus.InProgress, true)]).ViolationCode);

        Assert.True(RequestRules.CanClose(RequestStatus.Answered, [Conclusive()], [new RequirementFacts(RequirementStatus.Waived, true)]).IsAllowed);
        // A non-blocking requirement holds nothing open — one meaning of is_blocking everywhere (ADR-012).
        Assert.True(RequestRules.CanClose(RequestStatus.Answered, [Conclusive()], [new RequirementFacts(RequirementStatus.Open, false)]).IsAllowed);
    }

    [Fact]
    public void AChildRequestMayOnlySpringFromARequirementStillOpenToWork()
    {
        Assert.True(RequestRules.CanSpawnChildRequest(RequirementStatus.Open).IsAllowed);
        Assert.True(RequestRules.CanSpawnChildRequest(RequirementStatus.InProgress).IsAllowed);
        Assert.Equal("requirement.not_open_for_child_request", RequestRules.CanSpawnChildRequest(RequirementStatus.Fulfilled).ViolationCode);
    }

    [Fact]
    public void FulfilmentNeedsEvidenceOrAnExplicitNote()
    {
        Assert.Equal("requirement.fulfil_needs_evidence_or_note", RequirementRules.CanFulfill(RequirementStatus.Open, 0, null).ViolationCode);
        Assert.True(RequirementRules.CanFulfill(RequirementStatus.Open, 1, null).IsAllowed);
        Assert.True(RequirementRules.CanFulfill(RequirementStatus.InProgress, 0, "Sənəd təqdim edildi").IsAllowed);
    }

    [Fact]
    public void EveryTerminalOutcomeCarriesItsOwnMandatoryReason()
    {
        Assert.Equal("requirement.waiver_reason_required", RequirementRules.CanWaive(RequirementStatus.Open, null, "note").ViolationCode);
        Assert.Equal("requirement.note_required", RequirementRules.CanWaive(RequirementStatus.Open, "AUTHORISED_TO_PROCEED", " ").ViolationCode);
        Assert.Equal("requirement.void_reason_required", RequirementRules.CanVoid(RequirementStatus.Open, null, "note").ViolationCode);
        Assert.Equal("requirement.note_required", RequirementRules.CanVoid(RequirementStatus.Open, VoidReasonCodes.NoLongerRequired, null).ViolationCode);
        Assert.Equal("requirement.note_required", RequirementRules.CanFail(RequirementStatus.Open, null).ViolationCode);
    }

    [Fact]
    public void ATerminalRequirementIsNotResolvedTwice()
    {
        foreach (var terminal in new[] { RequirementStatus.Fulfilled, RequirementStatus.Waived, RequirementStatus.Void, RequirementStatus.Failed })
        {
            Assert.Equal("requirement.not_resolvable", RequirementRules.CanFulfill(terminal, 1, "note").ViolationCode);
            Assert.Equal("requirement.not_resolvable", RequirementRules.CanWaive(terminal, "AUTHORISED_TO_PROCEED", "note").ViolationCode);
        }

        // There is no terminal-to-terminal transition: correcting one is Q7, which this slice does not implement.
        Assert.Equal("requirement.not_open", RequirementRules.CanStart(RequirementStatus.Fulfilled).ViolationCode);
    }

    [Fact]
    public void ClosedAndCancelledCasesTakeNoNewWork()
    {
        Assert.Equal("case.not_open_to_work", CaseRules.AcceptsNewWork(CaseLifecycleState.Closed).ViolationCode);
        Assert.Equal("case.not_open_to_work", CaseRules.AcceptsNewWork(CaseLifecycleState.Cancelled).ViolationCode);
        Assert.True(CaseRules.AcceptsNewWork(CaseLifecycleState.Active).IsAllowed);
        Assert.True(CaseRules.AcceptsNewWork(CaseLifecycleState.OnHold).IsAllowed);
    }

    [Fact]
    public void ACaseActivatesOnRealWorkOnlyWhenSomebodyIsResponsible()
    {
        Assert.True(CaseRules.ActivatesOnSubstantiveRecord(CaseLifecycleState.Registered, hasCurrentResponsible: true));
        Assert.False(CaseRules.ActivatesOnSubstantiveRecord(CaseLifecycleState.Registered, hasCurrentResponsible: false));
        Assert.False(CaseRules.ActivatesOnSubstantiveRecord(CaseLifecycleState.Active, hasCurrentResponsible: true));
    }
}
