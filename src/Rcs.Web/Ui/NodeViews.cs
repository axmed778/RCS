using Rcs.Application.Cases;
using Rcs.Domain.Authorization;

namespace Rcs.Web.Ui;

/// <summary>One request and the workspace it belongs to, for the recursive tree partial.</summary>
public sealed record RequestNodeView(RequestNode Request, CaseWorkspace Workspace)
{
    public bool May(BusinessAction action) => AuthorizationPolicy.Decide(Workspace.Actor, action, Workspace.Relationship).IsAllowed;
}

/// <summary>One requirement and the workspace it belongs to.</summary>
public sealed record RequirementNodeView(RequirementNode Requirement, CaseWorkspace Workspace)
{
    public bool May(BusinessAction action) => AuthorizationPolicy.Decide(Workspace.Actor, action, Workspace.Relationship).IsAllowed;
}
