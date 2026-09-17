using Rcs.Domain.Vocabulary;

namespace Rcs.UnitTests.Domain;

/// <summary>The stored codes are exactly the frozen vocabularies, in the frozen documents' order.</summary>
public sealed class ClosedVocabularyTests
{
    [Fact]
    public void CodesMatchTheFrozenDomainModel()
    {
        Assert.Equal(["REGISTERED", "ACTIVE", "ON_HOLD", "CLOSED", "CANCELLED"], VocabularyCodes.AllCodes<CaseLifecycleState>());
        Assert.Equal(["DRAFT", "SENT", "ANSWERED", "CLOSED", "WITHDRAWN", "VOID"], VocabularyCodes.AllCodes<RequestStatus>());
        Assert.Equal(["ACTIVE", "SUPERSEDED", "VOID"], VocabularyCodes.AllCodes<ResponseStatus>());
        Assert.Equal(["OPEN", "IN_PROGRESS", "FULFILLED", "WAIVED", "VOID", "FAILED"], VocabularyCodes.AllCodes<RequirementStatus>());
        Assert.Equal(["ACTIVE", "SUPERSEDED", "WITHDRAWN"], VocabularyCodes.AllCodes<DocumentVersionStatus>());
        Assert.Equal(["DRAFT", "ISSUED", "SUPERSEDED", "REVOKED", "VOID"], VocabularyCodes.AllCodes<FinalResultStatus>());
        Assert.Equal(["DRAFT", "REGISTERED", "SENT", "RECEIVED", "WITHDRAWN", "SUPERSEDED", "VOID"], VocabularyCodes.AllCodes<CorrespondenceStatus>());
        Assert.Equal(["IN", "OUT"], VocabularyCodes.AllCodes<CorrespondenceDirection>());
        Assert.Equal(["ACTIVE", "ENDED", "VOID"], VocabularyCodes.AllCodes<AssignmentStatus>());
        Assert.Equal(["USER", "SYSTEM", "JOB"], VocabularyCodes.AllCodes<ActorKind>());
    }

    [Fact]
    public void EveryCodeRoundTrips()
    {
        foreach (var state in Enum.GetValues<CaseLifecycleState>())
        {
            Assert.Equal(state, VocabularyCodes.FromCode<CaseLifecycleState>(state.ToCode()));
        }

        foreach (var status in Enum.GetValues<RequirementStatus>())
        {
            Assert.Equal(status, VocabularyCodes.FromCode<RequirementStatus>(status.ToCode()));
        }
    }

    [Fact]
    public void DefaultValueIsNotAValidState()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => default(CaseLifecycleState).ToCode());
    }

    [Theory]
    [InlineData("on_hold")] // codes are exact and case-sensitive
    [InlineData("OVERDUE")] // derived, never stored (DECISIONS.md ADR-029)
    [InlineData("")]
    public void UnknownCodesAreRejected(string code)
    {
        Assert.Throws<ArgumentException>(() => VocabularyCodes.FromCode<CaseLifecycleState>(code));
    }

    [Fact]
    public void TerminalStatesAreExactlyTheFrozenSets()
    {
        Assert.Equal(
            [RequirementStatus.Fulfilled, RequirementStatus.Waived, RequirementStatus.Void, RequirementStatus.Failed],
            Enum.GetValues<RequirementStatus>().Where(status => status.IsTerminal()));
        Assert.Equal(
            [RequestStatus.Closed, RequestStatus.Withdrawn, RequestStatus.Void],
            Enum.GetValues<RequestStatus>().Where(status => status.IsTerminal()));
    }
}
