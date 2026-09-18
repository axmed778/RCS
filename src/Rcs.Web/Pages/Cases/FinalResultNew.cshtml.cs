using Microsoft.AspNetCore.Mvc;
using Rcs.Application.Cases;
using Rcs.Application.Identifiers;
using Rcs.Application.Idempotency;
using Rcs.Application.Lifecycle;
using Rcs.Application.Lookups;
using Rcs.Web.Review;
using Rcs.Web.Ui;

namespace Rcs.Web.Pages.Cases;

/// <summary>
/// F1 — drafting the decision the case exists to produce (WORKFLOW.md §8.1). A draft has no effect on anything: it
/// does not block a branch and does not signal completion. Issuing it is a separate act, and the Head's alone.
/// </summary>
public sealed class FinalResultNewModel(
    ICaseQueries cases,
    ILifecycleQueries lifecycleQueries,
    ICaseLifecycleService lifecycle,
    ILookupQueries lookups,
    IIdGenerator ids,
    CurrentActor actor,
    UiText text) : ReviewPageModel(actor, text)
{
    public sealed class InputModel
    {
        public Guid OperationId { get; set; }

        public string? DecisionTypeCode { get; set; }

        public string? Summary { get; set; }

        public string? Reasoning { get; set; }

        /// <summary>Set when this draft replaces the result currently in force (WORKFLOW.md §8.6).</summary>
        public Guid? SupersedesFinalResultId { get; set; }
    }

    [BindProperty(SupportsGet = true)]
    public Guid CaseId { get; set; }

    [BindProperty]
    public InputModel Input { get; set; } = new();

    public CaseWorkspace? Workspace { get; private set; }

    public IReadOnlyList<LookupItem> DecisionTypes { get; private set; } = [];

    /// <summary>The result in force, when there is one: this draft would replace it.</summary>
    public FinalResultView? InForce { get; private set; }

    /// <summary>
    /// Requirements the case could not satisfy. WORKFLOW.md §8.5: a decision may be taken with a FAILED or WAIVED
    /// obligation, but never while it is invisible — the reasoning is expected to address them.
    /// </summary>
    public IReadOnlyList<RequirementNode> UnmetObligations { get; private set; } = [];

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
        Input.SupersedesFinalResultId = InForce?.Id;
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

        if (string.IsNullOrWhiteSpace(Input.DecisionTypeCode))
        {
            ModelState.AddModelError("Input.DecisionTypeCode", Text["Error.validation.required"]);
        }

        if (string.IsNullOrWhiteSpace(Input.Summary))
        {
            ModelState.AddModelError("Input.Summary", Text["Error.validation.required"]);
        }

        if (!ModelState.IsValid)
        {
            return Page();
        }

        var result = await lifecycle.DraftFinalResultAsync(Actor.Context, new DraftFinalResultCommand(
            new OperationId(Input.OperationId == Guid.Empty ? ids.NewId() : Input.OperationId),
            CaseId,
            Input.DecisionTypeCode!,
            Input.Summary!,
            Input.Reasoning,
            Input.SupersedesFinalResultId), HttpContext.RequestAborted);

        if (!result.Succeeded)
        {
            ApplyError(result.Error);
            return Page();
        }

        return ToWorkspace(CaseId, "final-result-" + result.Value);
    }

    private async Task<IActionResult?> LoadAsync()
    {
        var workspace = await cases.GetWorkspaceAsync(Actor.Context, CaseId, HttpContext.RequestAborted);
        if (!workspace.Succeeded)
        {
            return NotFound();
        }

        Workspace = workspace.Value;
        DecisionTypes = await lookups.ListActiveAsync(LookupKind.DecisionType, HttpContext.RequestAborted);
        InForce = (await lifecycleQueries.ListFinalResultsAsync(Actor.Context, CaseId, HttpContext.RequestAborted))
            .FirstOrDefault(result => result.Status == Rcs.Domain.Vocabulary.FinalResultStatus.Issued);
        UnmetObligations = Workspace!.AllRequirements
            .Where(requirement => requirement.Status is Rcs.Domain.Vocabulary.RequirementStatus.Failed or Rcs.Domain.Vocabulary.RequirementStatus.Waived)
            .ToArray();

        return null;
    }
}
