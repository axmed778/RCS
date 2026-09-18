using Rcs.Application.Cases;
using Rcs.Application.Documents;
using Rcs.Domain.Authorization;

namespace Rcs.Web.Ui;

/// <summary>One request and the workspace it belongs to, for the recursive tree partial.</summary>
public sealed record RequestNodeView(RequestNode Request, CaseWorkspace Workspace, CaseDocuments Documents)
{
    public bool May(BusinessAction action) => AuthorizationPolicy.Decide(Workspace.Actor, action, Workspace.Relationship).IsAllowed;
}

/// <summary>One requirement and the workspace it belongs to.</summary>
public sealed record RequirementNodeView(RequirementNode Requirement, CaseWorkspace Workspace, CaseDocuments Documents)
{
    public bool May(BusinessAction action) => AuthorizationPolicy.Decide(Workspace.Actor, action, Workspace.Relationship).IsAllowed;
}

public static class DocumentListViews
{
    /// <summary>The files of one context of <paramref name="workspace"/>, with its add actions when the actor may upload.</summary>
    public static DocumentListView ListFor(
        this CaseDocuments documents,
        CaseWorkspace workspace,
        Rcs.Domain.Documents.DocumentTargetKind kind,
        Guid id,
        string uploadRole,
        string? attachRole = null,
        bool canAdd = true,
        string? headingKey = null)
    {
        ArgumentNullException.ThrowIfNull(documents);
        ArgumentNullException.ThrowIfNull(workspace);
        return new DocumentListView(
            workspace.Header.Id,
            documents.For(kind, id),
            new DocumentTarget(kind, id),
            uploadRole,
            canAdd && AuthorizationPolicy.Decide(workspace.Actor, BusinessAction.UploadDocument, workspace.Relationship).IsAllowed,
            attachRole,
            headingKey);
    }
}

/// <summary>
/// The files of one business context, as the inspector shows them (DOCUMENT_MODEL.md §19): the letter and its
/// attachments as one group in the letter's own order, removed placements kept as secondary history, and the add
/// actions of that context — so a file is always added where it belongs, never through a generic area.
/// </summary>
/// <param name="UploadTarget">Where "upload" places a new file; null when this context takes no uploads.</param>
/// <param name="UploadRole">The role pre-selected on the upload form.</param>
/// <param name="AttachRole">When set, an "attach an existing file" action placing a version in this role.</param>
public sealed record DocumentListView(
    Guid CaseId,
    IReadOnlyList<DocumentPlacementView> Placements,
    DocumentTarget? UploadTarget,
    string? UploadRole,
    bool CanAdd,
    string? AttachRole = null,
    string? HeadingKey = null);
