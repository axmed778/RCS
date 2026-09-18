using Rcs.Application.Cases;
using Rcs.Domain.Vocabulary;
using Rcs.Domain.Workflow;

namespace Rcs.Web.Ui;

/// <summary>Display-only outcome colours. Never used for permissions, progress or workflow transitions.</summary>
public sealed record WorkflowAppearance(string Tone, string LabelKey)
{
    public static WorkflowAppearance ForResponse(ResponseNode response)
    {
        if (response.Status != ResponseStatus.Active)
        {
            return new("waiting", "ResponseStatus." + response.Status.ToCode());
        }

        return response.OutcomeCode switch
        {
            ResponseOutcomeCodes.Rejected => new("negative", "Graph.Negative"),
            ResponseOutcomeCodes.Conditional => new("conditional", "Graph.Conditional"),
            ResponseOutcomeCodes.Approved when response.Requirements.Any(IsOpen) => new("conditional", "Graph.Conditional"),
            ResponseOutcomeCodes.Approved => new("positive", "Graph.Positive"),
            _ => new("waiting", "ResponseOutcome." + response.OutcomeCode),
        };
    }

    public static WorkflowAppearance ForRequest(RequestNode request)
    {
        if (request.Status is RequestStatus.Withdrawn or RequestStatus.Void or RequestStatus.Draft)
        {
            return new("waiting", "RequestStatus." + request.Status.ToCode());
        }

        var active = request.Responses.Where(response => response.Status == ResponseStatus.Active).ToArray();
        if (RequestRules.HasConflict(active.Select(response => new ResponseFacts(response.Status, response.IsConclusive, response.OutcomeCode))))
        {
            return new("conditional", "Graph.Conflict");
        }

        foreach (var tone in new[] { "negative", "conditional", "positive" })
        {
            var appearance = active.Select(ForResponse).FirstOrDefault(item => item.Tone == tone);
            if (appearance is not null)
            {
                return appearance;
            }
        }

        return active.Length == 0
            ? new("waiting", "Graph.Waiting")
            : active.Any(response => response.OutcomeCode == ResponseOutcomeCodes.Undetermined)
                ? new("waiting", "Graph.NoVerdict")
                : new("waiting", "Graph.ResponseReceived");
    }

    private static bool IsOpen(RequirementNode requirement) =>
        requirement.Status is RequirementStatus.Open or RequirementStatus.InProgress;
}
