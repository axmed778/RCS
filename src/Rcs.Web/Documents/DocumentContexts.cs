using Rcs.Application.Cases;
using Rcs.Application.Documents;
using Rcs.Application.Lifecycle;
using Rcs.Domain.Documents;
using Rcs.Domain.Vocabulary;
using Rcs.Web.Ui;

namespace Rcs.Web.Documents;

/// <summary>One business context of a case a file can be placed in, named the way the workspace names it.</summary>
/// <param name="Anchor">The workspace node the context appears under, for returning to it after an action.</param>
public sealed record DocumentContextOption(DocumentTarget Target, string Label, string Anchor, IReadOnlyList<string> Roles)
{
    public string Value => DocumentTargetCodec.Format(Target);
}

/// <summary>
/// The contexts of one case: its letters (the initiating letter, each outgoing request letter, each incoming response
/// letter filed in this case), its requests, responses and requirements, its open decisions, and the case itself for
/// supporting material. A response letter filed in another case is not offered — it stays governed by its own case (A-6).
/// </summary>
public static class DocumentContexts
{
    private static readonly string[] AllRoles =
    [
        DocumentLinkRoleCodes.PrimaryLetter, DocumentLinkRoleCodes.Attachment, DocumentLinkRoleCodes.Annex,
        DocumentLinkRoleCodes.RequirementEvidence, DocumentLinkRoleCodes.FinalResultDocument,
        DocumentLinkRoleCodes.Supporting, DocumentLinkRoleCodes.WorkingCopy,
    ];

    public static IReadOnlyList<string> RolesFor(DocumentTargetKind kind) =>
        AllRoles.Where(role => DocumentRules.RoleFitsTarget(kind, role)).ToArray();

    public static IReadOnlyList<DocumentContextOption> Of(CaseWorkspace workspace, IReadOnlyList<FinalResultView> results, UiText text)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(results);
        ArgumentNullException.ThrowIfNull(text);

        var options = new List<DocumentContextOption>();
        var letters = new HashSet<Guid>();

        void Add(DocumentTargetKind kind, Guid id, string label, string anchor) =>
            options.Add(new DocumentContextOption(new DocumentTarget(kind, id), label, anchor, RolesFor(kind)));

        if (workspace.InitiatingLetter is { } initiating && letters.Add(initiating.Id))
        {
            Add(DocumentTargetKind.Correspondence, initiating.Id,
                $"{text["Documents.Context.IncomingLetter"]} {text.Text(initiating.LetterNumber)} · {text.Date(initiating.LetterDate)}", $"letter-{initiating.Id}");
        }

        foreach (var request in workspace.AllRequests)
        {
            if (request.DispatchLetter is { } outgoing && letters.Add(outgoing.Id))
            {
                Add(DocumentTargetKind.Correspondence, outgoing.Id,
                    $"{text["Documents.Context.OutgoingLetter"]} {text.Text(outgoing.RegistryNumber ?? outgoing.LetterNumber)} · {request.Target.Name}", $"request-{request.Id}");
            }

            foreach (var response in request.Responses)
            {
                if (response.Letter is { } incoming && letters.Add(incoming.Id))
                {
                    Add(DocumentTargetKind.Correspondence, incoming.Id,
                        $"{text["Documents.Context.ResponseLetter"]} {text.Text(incoming.LetterNumber)} · {incoming.Sender.Name}", $"response-{response.Id}");
                }
            }
        }

        foreach (var request in workspace.AllRequests)
        {
            Add(DocumentTargetKind.Request, request.Id, $"{text["Request.Title"]} {request.RequestNumber} — {request.Target.Name}", $"request-{request.Id}");
            foreach (var response in request.Responses.Where(response => response.Status != ResponseStatus.Void))
            {
                Add(DocumentTargetKind.Response, response.Id,
                    $"{text["Response.Title"]} — {text.Code("ResponseType", response.TypeCode)} · {request.Target.Name}", $"response-{response.Id}");
            }
        }

        foreach (var requirement in workspace.AllRequirements)
        {
            Add(DocumentTargetKind.Requirement, requirement.Id, $"{text["Requirement.Title"]} — {requirement.Title}", $"requirement-{requirement.Id}");
        }

        foreach (var result in results.Where(result => result.Status is FinalResultStatus.Draft or FinalResultStatus.Issued))
        {
            Add(DocumentTargetKind.FinalResult, result.Id, $"{text["FinalResult.Title"]} {result.ResultNumber}", $"final-result-{result.Id}");
        }

        Add(DocumentTargetKind.Case, workspace.Header.Id, $"{text["Workspace.Header.Case"]} {workspace.Header.CaseNumber}", $"case-{workspace.Header.Id}");
        return options;
    }
}
