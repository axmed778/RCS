using Rcs.Application.Cases;
using Rcs.Domain.Vocabulary;
using Rcs.Web.Ui;

namespace Rcs.IntegrationTests.Web;

/// <summary>Colour conveys the actual answer, not whether the request can be closed.</summary>
public sealed class WorkflowAppearanceTests
{
    private static ResponseNode Response(string outcome, ResponseStatus status = ResponseStatus.Active) =>
        new(Guid.Empty, Guid.Empty, null, "OPINION", outcome, true, null, status, null, null,
            DateTimeOffset.UtcNow, new UserRef(Guid.Empty, "Synthetic reviewer"), []);

    private static RequestNode Request(params ResponseNode[] responses) =>
        new(Guid.Empty, "TEST-1", new OrganizationRef(Guid.Empty, "Synthetic authority", "Synthetic authority"),
            "Synthetic request", null, RequestStatus.Answered, 1, null, null, null, null, responses);

    [Theory]
    [InlineData(ResponseOutcomeCodes.Approved, "positive")]
    [InlineData(ResponseOutcomeCodes.Rejected, "negative")]
    [InlineData(ResponseOutcomeCodes.Conditional, "conditional")]
    [InlineData(ResponseOutcomeCodes.NotApplicable, "waiting")]
    [InlineData(ResponseOutcomeCodes.Undetermined, "waiting")]
    public void OutcomeDeterminesTheColourEvenWhenAResponseIsConclusive(string outcome, string tone)
    {
        Assert.Equal(tone, WorkflowAppearance.ForResponse(Response(outcome)).Tone);
        Assert.Equal(tone, WorkflowAppearance.ForRequest(Request(Response(outcome))).Tone);
    }

    [Theory]
    [InlineData(ResponseStatus.Superseded)]
    [InlineData(ResponseStatus.Void)]
    public void InactiveResponsesDoNotColourTheRequest(ResponseStatus status)
    {
        var oldResponse = Response(ResponseOutcomeCodes.Rejected, status);
        Assert.Equal("waiting", WorkflowAppearance.ForResponse(oldResponse).Tone);
        Assert.Equal("positive", WorkflowAppearance.ForRequest(Request(oldResponse, Response(ResponseOutcomeCodes.Approved))).Tone);
    }

    [Fact]
    public void ConflictingConclusiveAnswersAreNotPresentedAsAnUnambiguousVerdict()
    {
        var result = WorkflowAppearance.ForRequest(Request(Response(ResponseOutcomeCodes.Approved), Response(ResponseOutcomeCodes.Rejected)));
        Assert.Equal("conditional", result.Tone);
        Assert.Equal("Graph.Conflict", result.LabelKey);
    }

    [Fact]
    public void AnOverdueUnansweredRequestIsWaitingRatherThanRejected()
    {
        var request = Request() with { Status = RequestStatus.Sent, DueAt = DateTimeOffset.UtcNow.AddDays(-10) };
        Assert.Equal(new WorkflowAppearance("waiting", "Graph.Waiting"), WorkflowAppearance.ForRequest(request));
    }

    [Fact]
    public void InformationalResponseIsNotMisrepresentedAsWaitingForAnAnswer()
    {
        Assert.Equal(new WorkflowAppearance("waiting", "Graph.ResponseReceived"),
            WorkflowAppearance.ForRequest(Request(Response(ResponseOutcomeCodes.NotApplicable))));
    }

    [Theory]
    [InlineData(RequestStatus.Withdrawn)]
    [InlineData(RequestStatus.Void)]
    public void InactiveRequestsKeepTheirStoredStatus(RequestStatus status)
    {
        var result = WorkflowAppearance.ForRequest(Request(Response(ResponseOutcomeCodes.Approved)) with { Status = status });
        Assert.Equal("waiting", result.Tone);
        Assert.Equal("RequestStatus." + status.ToCode(), result.LabelKey);
    }
}
