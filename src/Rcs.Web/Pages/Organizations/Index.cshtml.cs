using Rcs.Application.Organizations;
using Rcs.Web.Review;
using Rcs.Web.Ui;

namespace Rcs.Web.Pages.Organizations;

public sealed class IndexModel(IOrganizationService organizations, CurrentActor actor, UiText text) : ReviewPageModel(actor, text)
{
    public IReadOnlyList<OrganizationListItem> Organizations { get; private set; } = [];

    public async Task OnGetAsync()
    {
        if (HasActor)
        {
            Organizations = await organizations.ListAsync(HttpContext.RequestAborted);
        }
    }
}
