using Microsoft.AspNetCore.Mvc;
using Rcs.Application.Cases;
using Rcs.Application.Lookups;
using Rcs.Application.Workflow;
using Rcs.Web.Review;
using Rcs.Web.Ui;

namespace Rcs.Web.Pages.Cases;

/// <summary>
/// The four ways a requirement ends (WORKFLOW.md §5.2, Q3–Q6). They are deliberately separate: an obligation that
/// became irrelevant is voided, not "fulfilled", and a released one is waived by an authorised person with a reason.
/// Each carries its row version, so two people cannot resolve the same requirement twice.
/// </summary>
public sealed class RequirementResolveModel(
    ICaseQueries cases,
    IWorkflowService workflow,
    ILookupQueries lookups,
    CurrentActor actor,
    UiText text) : ReviewPageModel(actor, text)
{
    public sealed class InputModel
    {
        public int RowVersion { get; set; }

        public Guid? EvidenceResponseId { get; set; }

        public string? ResolutionNote { get; set; }

        public string? ReasonCode { get; set; }

        public Guid? VoidSourceResponseId { get; set; }
    }

    [BindProperty(SupportsGet = true)]
    public Guid CaseId { get; set; }

    [BindProperty(SupportsGet = true)]
    public Guid RequirementId { get; set; }

    [BindProperty(SupportsGet = true)]
    public string Action { get; set; } = "fulfil";

    [BindProperty]
    public InputModel Input { get; set; } = new();

    public CaseWorkspace? Workspace { get; private set; }

    public RequirementNode? Requirement { get; private set; }

    public IReadOnlyList<LookupItem> Reasons { get; private set; } = [];

    /// <summary>The ACTIVE responses of this case that can stand as evidence or as the later answer that voided a requirement.</summary>
    public IReadOnlyList<ResponseNode> CaseResponses { get; private set; } = [];

    public async Task<IActionResult> OnGetAsync()
    {
        if (!HasActor)
        {
            return Page();
        }

        if (await LoadAsync() is { } failure)
        {
            return failure;
        }

        Input.RowVersion = Requirement!.RowVersion;
        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        if (!HasActor)
        {
            return Page();
        }

        if (await LoadAsync() is { } failure)
        {
            return failure;
        }

        var result = Action switch
        {
            "fulfil" => await workflow.FulfillRequirementAsync(Actor.Context,
                new FulfillRequirementCommand(CaseId, RequirementId, Input.RowVersion, Input.EvidenceResponseId, Input.ResolutionNote), HttpContext.RequestAborted),
            "waive" => await workflow.WaiveRequirementAsync(Actor.Context,
                new WaiveRequirementCommand(CaseId, RequirementId, Input.RowVersion, Input.ReasonCode ?? string.Empty, Input.ResolutionNote ?? string.Empty), HttpContext.RequestAborted),
            "void" => await workflow.VoidRequirementAsync(Actor.Context,
                new VoidRequirementCommand(CaseId, RequirementId, Input.RowVersion, Input.ReasonCode ?? string.Empty, Input.ResolutionNote ?? string.Empty, Input.VoidSourceResponseId), HttpContext.RequestAborted),
            "fail" => await workflow.FailRequirementAsync(Actor.Context,
                new FailRequirementCommand(CaseId, RequirementId, Input.RowVersion, Input.ResolutionNote ?? string.Empty), HttpContext.RequestAborted),
            _ => null,
        };

        if (result is null)
        {
            return NotFound();
        }

        if (!result.Succeeded)
        {
            ApplyError(result.Error);
            return Page();
        }

        return ToWorkspace(CaseId, "requirement-" + RequirementId);
    }

    private async Task<IActionResult?> LoadAsync()
    {
        if (Action is not ("fulfil" or "waive" or "void" or "fail"))
        {
            return NotFound();
        }

        var workspace = await cases.GetWorkspaceAsync(Actor.Context, CaseId, HttpContext.RequestAborted);
        if (!workspace.Succeeded)
        {
            return NotFound();
        }

        Workspace = workspace.Value;
        Requirement = Workspace!.FindRequirement(RequirementId);
        if (Requirement is null)
        {
            return NotFound();
        }

        CaseResponses = Workspace.AllResponses.Where(response => response.Status == Domain.Vocabulary.ResponseStatus.Active).ToArray();
        Reasons = Action switch
        {
            "waive" => await lookups.ListActiveAsync(LookupKind.WaiverReason, HttpContext.RequestAborted),
            "void" => await lookups.ListActiveAsync(LookupKind.VoidReason, HttpContext.RequestAborted),
            _ => [],
        };

        return null;
    }

    public string TitleKey => $"Resolve.{Title()}.Title";

    public string IntroKey => $"Resolve.{Title()}.Intro";

    public string ReasonPrefix => Action == "waive" ? "WaiverReason" : "VoidReason";

    private string Title() => Action switch
    {
        "waive" => "Waive",
        "void" => "Void",
        "fail" => "Fail",
        _ => "Fulfill",
    };
}
