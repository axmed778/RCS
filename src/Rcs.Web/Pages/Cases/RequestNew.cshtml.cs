using Microsoft.AspNetCore.Mvc;
using Rcs.Application.Cases;
using Rcs.Application.Common;
using Rcs.Application.Identifiers;
using Rcs.Application.Idempotency;
using Rcs.Application.Organizations;
using Rcs.Application.Workflow;
using Rcs.Domain.Workflow;
using Rcs.Web.Review;
using Rcs.Web.Ui;

namespace Rcs.Web.Pages.Cases;

/// <summary>
/// Registers a request together with the outgoing letter that already carried it (WORKFLOW.md R1b). With a source
/// requirement it is a child request, and the causal link is recorded at creation and never changed afterwards.
/// </summary>
public sealed class RequestNewModel(
    ICaseQueries cases,
    IWorkflowService workflow,
    IOrganizationService organizations,
    IIdGenerator ids,
    BusinessCalendar calendar,
    CurrentActor actor,
    UiText text) : ReviewPageModel(actor, text)
{
    /// <summary>The department's rule, exposed so the form can recompute the suggestion when the sent date changes.</summary>
    public static int DeadlineDays => RequestDeadline.DefaultDays;

    public sealed class InputModel
    {
        public Guid OperationId { get; set; }

        public Guid TargetOrganizationId { get; set; }

        public string? Subject { get; set; }

        public string? OutgoingLetterNumber { get; set; }

        public DateOnly? SentDate { get; set; }

        public DateOnly? DueDate { get; set; }

        public string? RequestedItemsNote { get; set; }
    }

    [BindProperty(SupportsGet = true)]
    public Guid CaseId { get; set; }

    [BindProperty(SupportsGet = true)]
    public Guid? RequirementId { get; set; }

    [BindProperty]
    public InputModel Input { get; set; } = new();

    public CaseWorkspace? Workspace { get; private set; }

    public RequirementNode? SourceRequirement { get; private set; }

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

        // ADR-041: the letter usually went out the day it is registered, and the deadline is suggested from that
        // date. Both fields stay editable — a back-dated registration simply produces an earlier, truthful deadline.
        Input.SentDate = calendar.Today;
        Input.DueDate = RequestDeadline.Suggest(calendar.Today);

        if (SourceRequirement?.AddressedTo is { } addressedTo)
        {
            Input.TargetOrganizationId = addressedTo.Id;
            Input.Subject = SourceRequirement.Title;
        }

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

        if (Input.SentDate is null)
        {
            ModelState.AddModelError("Input.SentDate", Text["Error.validation.required"]);
        }

        if (Input.TargetOrganizationId == Guid.Empty)
        {
            ModelState.AddModelError("Input.TargetOrganizationId", Text["Error.validation.required"]);
        }

        if (!ModelState.IsValid)
        {
            return Page();
        }

        var result = await workflow.RegisterRequestAsync(Actor.Context, new RegisterRequestCommand(
            new OperationId(Input.OperationId == Guid.Empty ? ids.NewId() : Input.OperationId),
            CaseId,
            Input.TargetOrganizationId,
            Input.Subject ?? string.Empty,
            Input.OutgoingLetterNumber ?? string.Empty,
            Input.SentDate!.Value,
            Input.DueDate,
            RequirementId,
            Input.RequestedItemsNote), HttpContext.RequestAborted);

        if (!result.Succeeded)
        {
            ApplyError(result.Error);
            return Page();
        }

        return ToWorkspace(CaseId, "request-" + result.Value);
    }

    private async Task<IActionResult?> LoadAsync()
    {
        var workspace = await cases.GetWorkspaceAsync(Actor.Context, CaseId, HttpContext.RequestAborted);
        if (!workspace.Succeeded)
        {
            return NotFound();
        }

        Workspace = workspace.Value;
        Organizations = await organizations.ListActiveExternalAsync(HttpContext.RequestAborted);

        if (RequirementId is { } requirementId)
        {
            SourceRequirement = Workspace!.FindRequirement(requirementId);
            if (SourceRequirement is null)
            {
                return NotFound();
            }
        }

        return null;
    }
}
