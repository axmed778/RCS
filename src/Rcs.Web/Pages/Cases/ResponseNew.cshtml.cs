using Microsoft.AspNetCore.Mvc;
using Rcs.Application.Cases;
using Rcs.Application.Identifiers;
using Rcs.Application.Idempotency;
using Rcs.Application.Lookups;
using Rcs.Application.Workflow;
using Rcs.Domain.Vocabulary;
using Rcs.Web.Review;
using Rcs.Web.Ui;

namespace Rcs.Web.Pages.Cases;

/// <summary>
/// Registers an incoming response to one request (WORKFLOW.md §4.1). Type and outcome are separate axes and both are
/// required; conclusiveness is the one judgement the clerk makes, and the request's state follows from it.
/// </summary>
public sealed class ResponseNewModel(
    ICaseQueries cases,
    IWorkflowService workflow,
    ILookupQueries lookups,
    IIdGenerator ids,
    CurrentActor actor,
    UiText text) : ReviewPageModel(actor, text)
{
    public sealed class InputModel
    {
        public Guid OperationId { get; set; }

        public string? IncomingLetterNumber { get; set; }

        public string? IncomingRegistryNumber { get; set; }

        public DateOnly? LetterDate { get; set; }

        public DateOnly? ReceivedDate { get; set; }

        public string ResponseTypeCode { get; set; } = "OPINION";

        public string ResponseOutcomeCode { get; set; } = ResponseOutcomeCodes.Approved;

        public bool IsConclusive { get; set; } = true;

        public string? Summary { get; set; }
    }

    [BindProperty(SupportsGet = true)]
    public Guid CaseId { get; set; }

    [BindProperty(SupportsGet = true)]
    public Guid RequestId { get; set; }

    [BindProperty]
    public InputModel Input { get; set; } = new();

    public RequestNode? TargetRequest { get; private set; }

    public IReadOnlyList<LookupItem> Types { get; private set; } = [];

    public IReadOnlyList<LookupItem> Outcomes { get; private set; } = [];

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

        if (Input.LetterDate is null)
        {
            ModelState.AddModelError("Input.LetterDate", Text["Error.validation.required"]);
        }

        if (!ModelState.IsValid)
        {
            return Page();
        }

        var result = await workflow.RegisterResponseAsync(Actor.Context, new RegisterResponseCommand(
            new OperationId(Input.OperationId == Guid.Empty ? ids.NewId() : Input.OperationId),
            CaseId,
            RequestId,
            Input.IncomingLetterNumber ?? string.Empty,
            Input.IncomingRegistryNumber,
            Input.LetterDate!.Value,
            Input.ReceivedDate,
            Input.ResponseTypeCode,
            Input.ResponseOutcomeCode,
            Input.IsConclusive,
            Input.Summary), HttpContext.RequestAborted);

        if (!result.Succeeded)
        {
            ApplyError(result.Error);
            return Page();
        }

        // A conditional or additional-requirement answer normally imposes conditions: go straight to recording them.
        var imposesConditions = Input.ResponseOutcomeCode == ResponseOutcomeCodes.Conditional || Input.ResponseTypeCode == "ADDITIONAL_REQUIREMENT";
        return imposesConditions
            ? Redirect($"/cases/{CaseId}/responses/{result.Value}/requirements/new")
            : ToWorkspace(CaseId, "response-" + result.Value);
    }

    private async Task<IActionResult?> LoadAsync()
    {
        var workspace = await cases.GetWorkspaceAsync(Actor.Context, CaseId, HttpContext.RequestAborted);
        if (!workspace.Succeeded)
        {
            return NotFound();
        }

        TargetRequest = workspace.Value!.FindRequest(RequestId);
        if (TargetRequest is null)
        {
            return NotFound();
        }

        Types = await lookups.ListActiveAsync(LookupKind.ResponseType, HttpContext.RequestAborted);
        Outcomes = await lookups.ListActiveAsync(LookupKind.ResponseOutcome, HttpContext.RequestAborted);
        return null;
    }
}
