using System.Globalization;
using Microsoft.AspNetCore.DataProtection;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Localization;
using Microsoft.AspNetCore.RateLimiting;
using Rcs.Infrastructure;
using Rcs.Infrastructure.Configuration;
using Rcs.Web.Authentication;
using Rcs.Web.Configuration;
using Rcs.Web.Review;

namespace Rcs.Web.Hosting;

/// <summary>The composition root of the web application.</summary>
public static class WebHostComposition
{
    private const string ReadyTag = "ready";

    public static WebApplication Build(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        builder.Configuration.AddRcsSecretsFile();

        builder.Services.AddRcsInfrastructure(builder.Configuration);
        builder.Services.AddOptions<HostingOptions>()
            .Bind(builder.Configuration.GetSection(HostingOptions.SectionName));

        // The Development-only review scaffold. Outside Development the host refuses to start rather than quietly
        // ignoring the setting, so a pilot or production server can never be left with a way in that has no password.
        var review = builder.Configuration.GetSection(ReviewOptions.SectionName).Get<ReviewOptions>() ?? new ReviewOptions();
        if (review.Enabled && !builder.Environment.IsDevelopment())
        {
            throw new ReviewModeNotAllowedException(builder.Environment.EnvironmentName);
        }

        builder.Services.AddOptions<ReviewOptions>().Bind(builder.Configuration.GetSection(ReviewOptions.SectionName));
        if (builder.Configuration["Rcs:Hosting:DataProtectionKeysPath"] is { Length: > 0 } keysPath)
        {
            builder.Services.AddDataProtection().SetApplicationName("RCS")
                .PersistKeysToFileSystem(new DirectoryInfo(keysPath));
        }

        builder.Services.AddHttpContextAccessor();
        builder.Services.AddScoped<CurrentActor>();

        // Real authentication is the normal state. It is only stood down for the review scaffold, which cannot exist
        // outside Development (checked above).
        var authenticationEnabled = !review.Enabled;
        builder.Services.AddRcsAuthentication(builder.Configuration, authenticationEnabled);
        if (authenticationEnabled)
        {
            builder.Services.AddRateLimiter(options =>
            {
                var settings = builder.Configuration.GetSection(LocalAuthenticationOptions.SectionName).Get<LocalAuthenticationOptions>() ?? new LocalAuthenticationOptions();
                options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

                // Per-source limiting on sign-in only, on top of the per-account lock (SECURITY.md §6.4). Nothing else
                // in a LAN application of this size benefits from a global limiter.
                options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
                    HttpMethods.IsPost(context.Request.Method) && (context.Request.Path.StartsWithSegments("/login") || context.Request.Path.StartsWithSegments("/account/password"))
                        ? RateLimitPartition.GetFixedWindowLimiter(
                            context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                            _ => new FixedWindowRateLimiterOptions
                            {
                                PermitLimit = Math.Max(1, settings.AttemptsPerMinutePerHost),
                                Window = TimeSpan.FromMinutes(1),
                                QueueLimit = 0,
                            })
                        : RateLimitPartition.GetNoLimiter("unlimited"));
            });
        }

        // Server-rendered pages with a small vendored stylesheet and script; no SPA, no npm, no CDN (ADR-003).
        builder.Services.AddRazorPages();
        builder.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        builder.Services.AddSingleton<Rcs.Web.Ui.UiText>();

        // Refuses to start unless the schema is exactly the one this release expects.
        builder.Services.AddHostedService<SchemaCompatibilityGate>();

        // Refuses to start a pilot or production server configured to keep real documents inside the release directory.
        builder.Services.AddHostedService<DeploymentSafetyGate>();
        builder.Services.AddHostedService<Rcs.Web.Documents.TemporaryUploadSweeper>();
        builder.Services.AddHostedService<Rcs.Web.Previews.PreviewService>();
        builder.Services.AddHealthChecks()
            .AddCheck<DatabaseSchemaHealthCheck>("database-schema", tags: [ReadyTag]);

        builder.Services.Configure<ForwardedHeadersOptions>(options =>
        {
            // Default trust is loopback only: the bundled nginx is on this host. Never trust arbitrary LAN proxies.
            options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
            options.ForwardLimit = 1;
        });

        var app = builder.Build();
        app.UseForwardedHeaders();

        app.UseRequestLocalization(new RequestLocalizationOptions
        {
            // Azerbaijani only in this build; the localisation structure is ready for more (PROJECT.md §16).
            DefaultRequestCulture = new RequestCulture("az-Latn-AZ"),
            SupportedCultures = [CultureInfo.GetCultureInfo("az-Latn-AZ")],
            SupportedUICultures = [CultureInfo.GetCultureInfo("az-Latn-AZ")],
        });

        app.Use(async (context, next) =>
        {
            // Nothing user-supplied is rendered inline and no asset is fetched from anywhere but this host
            // (SECURITY.md §10.2, §18.3).
            var headers = context.Response.Headers;
            headers["Content-Security-Policy"] = "default-src 'self'; img-src 'self' data:; style-src 'self'; script-src 'self'; form-action 'self'; frame-ancestors 'none'; base-uri 'self'";
            headers["X-Content-Type-Options"] = "nosniff";
            headers["Referrer-Policy"] = "same-origin";
            headers.CacheControl = "no-store";
            await next();
        });

        app.UseStaticFiles();

        // Technical endpoints. Responses carry a status word, never internal detail, and never need a session.
        app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false }).AllowAnonymous();
        app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = check => check.Tags.Contains(ReadyTag) }).AllowAnonymous();

        if (authenticationEnabled)
        {
            app.UseRateLimiter();
            app.UseAuthentication();
            app.UseAuthorization();

            // A first-use or reset credential must be replaced before anything else happens (SECURITY.md §6.3).
            app.UseMiddleware<PasswordChangeRequiredMiddleware>();
        }
        else
        {
            app.UseMiddleware<ReviewActorMiddleware>();
        }

        app.MapRazorPages();
        Rcs.Web.Documents.DocumentEndpoints.MapDocumentEndpoints(app);
        Rcs.Web.Previews.PreviewEndpoints.Map(app);

        return app;
    }
}
