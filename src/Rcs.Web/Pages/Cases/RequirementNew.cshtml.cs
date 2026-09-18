using Microsoft.AspNetCore.Mvc;
using Rcs.Application.Cases;
using Rcs.Application.Identifiers;
using Rcs.Application.Idempotency;
using Rcs.Application.Organizations;
using Rcs.Application.Workflow;
using Rcs.Web.Review;
using Rcs.Web.Ui;

namespace Rcs.Web.Pages.Cases;

/// <summary>
/// Records a condition imposed by a response as a requirement (WORKFLOW.md §4.4). The response that imposed it is the
/// requirement's reason for existing and is never editable afterwards.
/// </summary>
public sealed class RequirementNewModel(
    ICaseQueries cases,
    IWorkflowService workflow,
    IOrganizationService organizations,
    IIdGenerator ids,
    CurrentActor actor,
    UiText text) : ReviewPageModel(actor, text)
{
    public sealed class InputModel
    {
        public Guid OperationId { get; set; }

        public string? Title { get; set; }

        public string? Description { get; set; }

        public bool IsBlocking { get; set; } = true;

        public DateOnly? DueDate { get; set; }

        public Guid? AddressedToOrganizationId { get; set; }
    }

    [BindProperty(SupportsGet = true)]
    public Guid CaseId { get; set; }

    [BindProperty(SupportsGet = true)]
    public Guid ResponseId { get; set; }

    [BindProperty]
    public InputModel Input { get; set; } = new();

    public ResponseNode? SourceResponse { get; private set; }

    public IReadOnlyList<OrganizationOption> Organizations { get; private set; } = [];

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

        Input.OperationId = ids.NewId();
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

        if (!ModelState.IsValid)
        {
            return Page();
        }

        var result = await workflow.CreateRequirementAsync(Actor.Context, new CreateRequirementCommand(
            new OperationId(Input.OperationId == Guid.Empty ? ids.NewId() : Input.OperationId),
            CaseId,
            ResponseId,
            Input.Title ?? string.Empty,
            Input.Description,
            Input.IsBlocking,
            Input.DueDate,
            Input.AddressedToOrganizationId), HttpContext.RequestAborted);

        if (!result.Succeeded)
        {
            ApplyError(result.Error);
            return Page();
        }

        return ToWorkspace(CaseId, "requirement-" + result.Value);
    }

    private async Task<IActionResult?> LoadAsync()
    {
        var workspace = await cases.GetWorkspaceAsync(Actor.Context, CaseId, HttpContext.RequestAborted);
        if (!workspace.Succeeded)
        {
            return NotFound();
        }

        SourceResponse = workspace.Value!.FindResponse(ResponseId);
        if (SourceResponse is null)
        {
            return NotFound();
        }

        Organizations = await organizations.ListActiveExternalAsync(HttpContext.RequestAborted);
        return null;
    }
}
