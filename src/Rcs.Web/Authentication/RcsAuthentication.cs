using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.Extensions.Options;
using Rcs.Application.Identity;
using Rcs.Infrastructure.Configuration;
using Rcs.Web.Review;

namespace Rcs.Web.Authentication;

/// <summary>
/// Cookie authentication over the local credential store (ADR-033; SECURITY.md §6, §8). The protected cookie carries a session identifier and display identity:
/// role authority is never in the ticket, it is read from the database on every request, so a
/// suspension, a revoked session or a removed role takes effect on the next click rather than at expiry.
/// </summary>
public static class RcsAuthentication
{
    public const string Scheme = CookieAuthenticationDefaults.AuthenticationScheme;

    public const string SessionClaim = "rcs:sid";

    /// <summary>Set on the request when the signed-in user must replace a temporary or first-use credential.</summary>
    public const string MustChangePasswordItem = "rcs.auth.must_change_password";

    public const string CookieName = "rcs_session";

    /// <param name="enabled">
    /// False only for the Development review scaffold, which supplies its own actor and has no credentials. The
    /// scheme is still registered so sign-out and the pages that reference it resolve, but nothing is required.
    /// </param>
    public static IServiceCollection AddRcsAuthentication(this IServiceCollection services, IConfiguration configuration, bool enabled)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        // The cookie's own settings are applied from the bound options, not from a value read here: configuration
        // sources added after this call (a host that layers its own, a test host) must still be honoured.
        services.AddOptions<CookieAuthenticationOptions>(Scheme)
            .Configure<IOptions<LocalAuthenticationOptions>>((options, settings) =>
            {
                options.Cookie.SecurePolicy = settings.Value.RequireSecureCookie ? CookieSecurePolicy.Always : CookieSecurePolicy.SameAsRequest;
                options.ExpireTimeSpan = TimeSpan.FromHours(Math.Max(1, settings.Value.SessionLifetimeHours));
            });

        services.AddAuthentication(Scheme).AddCookie(Scheme, options =>
        {
            options.Cookie.Name = CookieName;
            options.Cookie.HttpOnly = true;
            options.Cookie.SameSite = SameSiteMode.Strict;
            options.Cookie.IsEssential = true;
            options.SlidingExpiration = false;
            options.LoginPath = "/login";
            options.LogoutPath = "/logout";
            options.AccessDeniedPath = "/login";
            options.ReturnUrlParameter = "returnUrl";
            options.Events = new CookieAuthenticationEvents
            {
                // The one place a session becomes an actor. Everything downstream reads CurrentActor.
                OnValidatePrincipal = async context =>
                {
                    var sessionId = context.Principal?.FindFirstValue(SessionClaim);
                    if (!Guid.TryParse(sessionId, out var session))
                    {
                        context.RejectPrincipal();
                        return;
                    }

                    var authentication = context.HttpContext.RequestServices.GetRequiredService<ILocalAuthenticationService>();
                    var live = await authentication.ValidateSessionAsync(session, ClientHost(context.HttpContext), context.HttpContext.RequestAborted);
                    if (live is null)
                    {
                        context.RejectPrincipal();
                        await context.HttpContext.SignOutAsync(Scheme);
                        return;
                    }

                    context.HttpContext.Items[CurrentActor.ItemKey] = live.Profile;
                    context.HttpContext.Items[CurrentActor.SessionItemKey] = live.SessionId;
                    context.HttpContext.Items[MustChangePasswordItem] = live.MustChangePassword;
                },

                // A LAN application answering an unauthenticated API call with an HTML login page helps nobody.
                OnRedirectToLogin = context =>
                {
                    if (context.Request.Headers.Accept.Any(value => value?.Contains("application/json", StringComparison.OrdinalIgnoreCase) == true))
                    {
                        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                        return Task.CompletedTask;
                    }

                    context.Response.Redirect(context.RedirectUri);
                    return Task.CompletedTask;
                },
            };
        });

        // Deny by default: every page and endpoint needs a session unless it says otherwise (/login, /health).
        services.AddAuthorization(options =>
        {
            if (enabled)
            {
                options.FallbackPolicy = new Microsoft.AspNetCore.Authorization.AuthorizationPolicyBuilder(Scheme).RequireAuthenticatedUser().Build();
            }
        });
        return services;
    }

    /// <summary>The claims of a signed-in session. No role claim: roles are read at action time, never cached in a ticket.</summary>
    public static ClaimsPrincipal PrincipalFor(ActorProfile profile, Guid sessionId)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var identity = new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, profile.UserId.ToString()),
            new Claim(ClaimTypes.Name, profile.Username),
            new Claim(SessionClaim, sessionId.ToString()),
        ], Scheme);
        return new ClaimsPrincipal(identity);
    }

    public static string? ClientHost(HttpContext context) => context.Connection.RemoteIpAddress?.ToString();
}

/// <summary>
/// Sends a signed-in user who still holds a temporary or first-use credential to the password page, and lets them do
/// nothing else until it is replaced (SECURITY.md §6.3 "first login", §6.7 step 5).
/// </summary>
public sealed class PasswordChangeRequiredMiddleware(RequestDelegate next)
{
    private static readonly string[] Allowed = ["/account/password", "/logout", "/login", "/health", "/css", "/js", "/favicon.ico"];

    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var path = context.Request.Path.Value ?? "/";
        if (context.Items.TryGetValue(RcsAuthentication.MustChangePasswordItem, out var flag) && flag is true
            && !Allowed.Any(prefix => path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
        {
            context.Response.Redirect("/account/password");
            return;
        }

        await next(context);
    }
}
