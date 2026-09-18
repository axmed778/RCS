using Rcs.Application.Cases;
using Rcs.Web.Review;
using Rcs.Web.Ui;

namespace Rcs.Web.Pages.Cases;

public sealed class IndexModel(ICaseQueries cases, CurrentActor actor, UiText text) : ReviewPageModel(actor, text)
{
    public IReadOnlyList<CaseListItem> Cases { get; private set; } = [];

    public async Task OnGetAsync()
    {
        if (HasActor)
        {
            Cases = await cases.ListAsync(Actor.Context, HttpContext.RequestAborted);
        }
    }
}
