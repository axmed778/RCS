using Microsoft.AspNetCore.Mvc;
using Rcs.Application.Lookups;
using Rcs.Application.Organizations;
using Rcs.Web.Review;
using Rcs.Web.Ui;

namespace Rcs.Web.Pages.Organizations;

public sealed class NewModel(IOrganizationService organizations, ILookupQueries lookups, CurrentActor actor, UiText text) : ReviewPageModel(actor, text)
{
    public sealed class InputModel
    {
        public string? OfficialName { get; set; }

        public string? ShortName { get; set; }

        public string TypeCode { get; set; } = "STATE_AUTHORITY";

        public string? RegistrationCode { get; set; }

        public string? Notes { get; set; }
    }

    [BindProperty]
    public InputModel Input { get; set; } = new();

    public IReadOnlyList<LookupItem> Types { get; private set; } = [];

    public async Task OnGetAsync() => await LoadAsync();

    public async Task<IActionResult> OnPostAsync()
    {
        if (!HasActor)
        {
            return Page();
        }

        await LoadAsync();
        if (!ModelState.IsValid)
        {
            return Page();
        }

        var result = await organizations.CreateAsync(Actor.Context, new CreateOrganizationCommand(
            Input.OfficialName ?? string.Empty,
            Input.ShortName,
            Input.TypeCode,
            Input.RegistrationCode,
            Input.Notes), HttpContext.RequestAborted);

        if (!result.Succeeded)
        {
            ApplyError(result.Error);
            return Page();
        }

        return Redirect("/organizations");
    }

    private async Task LoadAsync()
    {
        if (HasActor)
        {
            Types = await lookups.ListActiveAsync(LookupKind.OrganizationType, HttpContext.RequestAborted);
        }
    }
}
