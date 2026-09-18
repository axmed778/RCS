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

    /// <summary>The synthetic user every action is attributed to. Created by the demo seed.</summary>
    public string ActorUsername { get; set; } = "review.demo";
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

        var profile = await users.FindByUsernameAsync(options.ActorUsername, context.RequestAborted);
        if (profile is null)
        {
            logger.LogWarning("The review actor '{Username}' does not exist. Run the demo seed: Rcs.Web seed-demo.", options.ActorUsername);
        }
        else
        {
            context.Items[CurrentActor.ItemKey] = profile;
        }

        await next(context);
    }
}
