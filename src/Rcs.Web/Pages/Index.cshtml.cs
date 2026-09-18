using Rcs.Application.Cases;
using Rcs.Web.Review;
using Rcs.Web.Ui;

namespace Rcs.Web.Pages;

public sealed class IndexModel(ICaseQueries cases, CurrentActor actor, UiText text) : ReviewPageModel(actor, text)
{
    public DashboardSummary? Summary { get; private set; }

    public async Task OnGetAsync()
    {
        if (HasActor)
        {
            Summary = await cases.GetDashboardAsync(Actor.Context, HttpContext.RequestAborted);
        }
    }
}
