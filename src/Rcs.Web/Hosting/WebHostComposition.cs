using System.Globalization;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Localization;
using Rcs.Infrastructure;
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

        var review = builder.Configuration.GetSection(ReviewOptions.SectionName).Get<ReviewOptions>() ?? new ReviewOptions();
        if (review.Enabled && !builder.Environment.IsDevelopment())
        {
            throw new ReviewModeNotAllowedException(builder.Environment.EnvironmentName);
        }

        builder.Services.AddOptions<ReviewOptions>().Bind(builder.Configuration.GetSection(ReviewOptions.SectionName));
        builder.Services.AddHttpContextAccessor();
        builder.Services.AddScoped<CurrentActor>();

        // Server-rendered pages with a small vendored stylesheet and script; no SPA, no npm, no CDN (ADR-003).
        builder.Services.AddRazorPages();
        builder.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        builder.Services.AddSingleton<Rcs.Web.Ui.UiText>();

        // Refuses to start unless the schema is exactly the one this release expects.
        builder.Services.AddHostedService<SchemaCompatibilityGate>();
        builder.Services.AddHealthChecks()
            .AddCheck<DatabaseSchemaHealthCheck>("database-schema", tags: [ReadyTag]);

        var app = builder.Build();

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
            await next();
        });

        app.UseStaticFiles();

        // Technical endpoints. Responses carry a status word, never internal detail.
        app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false });
        app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = check => check.Tags.Contains(ReadyTag) });

        if (review.Enabled)
        {
            app.UseMiddleware<ReviewActorMiddleware>();
            app.MapRazorPages();
        }

        return app;
    }
}
