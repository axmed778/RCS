using Rcs.Application.Common;
using Rcs.Application.Identity;

namespace Rcs.Web.Review;

/// <summary>
/// <c>Rcs:Review</c> — the Development-only review build. It exists so the Case workflow can be demonstrated and
/// reviewed before authentication is built (ADR-033), and it is NOT an authentication implementation.
/// </summary>
/// <remarks>
/// When enabled, every request acts as one clearly identified synthetic user, and the pages carry a banner saying so.
/// The host refuses to start if it is enabled outside the Development environment, and the business pages are not
/// mapped at all when it is off — there is no other way in yet.
/// </remarks>
public sealed class ReviewOptions
{
    public const string SectionName = "Rcs:Review";

    public bool Enabled { get; set; }

    /// <summary>The synthetic user actions are attributed to by default — a Chief. Created by the demo seed.</summary>
    public string ActorUsername { get; set; } = "review.demo";

    /// <summary>
    /// The synthetic Head. Approving a final result and overriding a closure guard belong to the highest business
    /// authority alone (ADR-040; PERMISSIONS.md §23), so reviewing them needs a second identity rather than a Chief
    /// with extra powers. Whichever identity is selected, the audit records that user — the attribution is real.
    /// </summary>
    public string HeadUsername { get; set; } = "review.head";

    /// <summary>
    /// The only usernames the switch will accept. A cookie naming anyone else is ignored, so the switch can never
    /// become a way to act as an arbitrary user.
    /// </summary>
    public IReadOnlyList<string> SelectableUsernames => [ActorUsername, HeadUsername];
}

/// <summary>Thrown at startup when the review build is configured outside Development.</summary>
public sealed class ReviewModeNotAllowedException(string environmentName)
    : Exception($"Rcs:Review:Enabled is true in the '{environmentName}' environment. The review actor is Development-only and must never run elsewhere; use real authentication instead.")
{
    public string EnvironmentName { get; } = environmentName;
}

/// <summary>The actor of the current request, as established by the host. Null when no actor could be resolved.</summary>
public sealed class CurrentActor(IHttpContextAccessor accessor)
{
    internal const string ItemKey = "rcs.review.actor";

    /// <summary>The cookie naming which synthetic identity to act as. Development-only, and never a credential.</summary>
    public const string SelectionCookie = "rcs_review_actor";

    public ActorProfile? Profile => accessor.HttpContext?.Items.TryGetValue(ItemKey, out var value) == true ? value as ActorProfile : null;

    public bool IsAvailable => Profile is not null;

    public ActorProfile Require => Profile ?? throw new InvalidOperationException("No actor is available for this request.");

    public ActorContext Context => new(Require.UserId, accessor.HttpContext?.Connection.RemoteIpAddress?.ToString());
}

/// <summary>
/// Resolves the single synthetic review actor for every request, from the database, with the roles it holds now. It
/// signs nobody in, issues no session and accepts no credentials: it is a Development scaffold, not authentication.
/// </summary>
public sealed class ReviewActorMiddleware(RequestDelegate next, IUserDirectory users, Microsoft.Extensions.Options.IOptions<ReviewOptions> options, ILogger<ReviewActorMiddleware> logger)
{
    private readonly ReviewOptions options = options.Value;

    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        // Technical endpoints stay independent of the review scaffold.
        if (context.Request.Path.StartsWithSegments("/health"))
        {
            await next(context);
            return;
        }

        var username = Selected(context);
        var profile = await users.FindByUsernameAsync(username, context.RequestAborted);
        if (profile is null)
        {
            logger.LogWarning("The review actor '{Username}' does not exist. Run the demo seed: Rcs.Web seed-demo.", username);
        }
        else
        {
            context.Items[CurrentActor.ItemKey] = profile;
        }

        await next(context);
    }

    /// <summary>
    /// The identity this request acts as: the cookie's choice when it names one of the configured synthetic users,
    /// otherwise the default. An unknown name is ignored rather than trusted, so the cookie cannot widen anything.
    /// </summary>
    private string Selected(HttpContext context) =>
        context.Request.Cookies.TryGetValue(CurrentActor.SelectionCookie, out var requested)
        && options.SelectableUsernames.Contains(requested, StringComparer.Ordinal)
            ? requested
            : options.ActorUsername;
}
