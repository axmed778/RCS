using Microsoft.AspNetCore.Mvc;
using Rcs.Application.Cases;
using Rcs.Application.Identifiers;
using Rcs.Application.Idempotency;
using Rcs.Application.Identity;
using Rcs.Application.Organizations;
using Rcs.Domain.Authorization;
using Rcs.Web.Review;
using Rcs.Web.Ui;

namespace Rcs.Web.Pages.Cases;

/// <summary>
/// Registers a case from an incoming official letter that the external government system already received
/// (WORKFLOW.md §2.1). The form carries an operation id generated once, so a resubmitted form cannot create a second
/// case (ADR-020).
/// </summary>
public sealed class NewModel(
    ICaseService cases,
    IOrganizationService organizations,
    IUserDirectory users,
    IIdGenerator ids,
    CurrentActor actor,
    UiText text) : ReviewPageModel(actor, text)
{
    public sealed class InputModel
    {
        public Guid OperationId { get; set; }

        public Guid RequestingOrganizationId { get; set; }

        public string? Title { get; set; }

        public string? Subject { get; set; }

        public string? IncomingLetterNumber { get; set; }

        public string? IncomingRegistryNumber { get; set; }

        public DateOnly? LetterDate { get; set; }

        public DateOnly? ReceivedDate { get; set; }

        public Guid? ResponsibleUserId { get; set; }

        public string? Notes { get; set; }
    }

    [BindProperty]
    public InputModel Input { get; set; } = new();

    public IReadOnlyList<OrganizationOption> Organizations { get; private set; } = [];

    public IReadOnlyList<UserOption> Employees { get; private set; } = [];

    public bool MayAssign { get; private set; }

    public async Task<IActionResult> OnGetAsync()
    {
        if (!HasActor)
        {
            return Page();
        }

        await LoadAsync();
        Input.OperationId = ids.NewId();
        Input.LetterDate ??= null;
        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        if (!HasActor)
        {
            return Page();
        }

        await LoadAsync();

        if (Input.LetterDate is null)
        {
            ModelState.AddModelError("Input.LetterDate", Text["Error.validation.required"]);
        }

        if (Input.RequestingOrganizationId == Guid.Empty)
        {
            ModelState.AddModelError("Input.RequestingOrganizationId", Text["Error.validation.required"]);
        }

        if (!ModelState.IsValid)
        {
            return Page();
        }

        var result = await cases.CreateAsync(Actor.Context, new CreateCaseCommand(
            new OperationId(Input.OperationId == Guid.Empty ? ids.NewId() : Input.OperationId),
            Input.RequestingOrganizationId,
            Input.Title ?? string.Empty,
            Input.Subject,
            Input.IncomingLetterNumber ?? string.Empty,
            Input.IncomingRegistryNumber,
            Input.LetterDate!.Value,
            Input.ReceivedDate,
            MayAssign ? Input.ResponsibleUserId : null,
            Input.Notes), HttpContext.RequestAborted);

        if (!result.Succeeded)
        {
            ApplyError(result.Error);
            return Page();
        }

        return ToWorkspace(result.Value);
    }

    private async Task LoadAsync()
    {
        Organizations = await organizations.ListActiveExternalAsync(HttpContext.RequestAborted);
        Employees = await users.ListAssignableAsync(HttpContext.RequestAborted);
        MayAssign = AuthorizationPolicy.Decide(Actor.Require.Authority(), BusinessAction.AssignCase).IsAllowed;
    }
}
