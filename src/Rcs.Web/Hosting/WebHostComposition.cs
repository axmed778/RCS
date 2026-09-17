using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Rcs.Infrastructure;
using Rcs.Web.Configuration;

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

        // Refuses to start unless the schema is exactly the one this release expects.
        builder.Services.AddHostedService<SchemaCompatibilityGate>();
        builder.Services.AddHealthChecks()
            .AddCheck<DatabaseSchemaHealthCheck>("database-schema", tags: [ReadyTag]);

        var app = builder.Build();

        // Phase 1 exposes technical endpoints only. Responses carry a status word, never internal detail.
        app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false });
        app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = check => check.Tags.Contains(ReadyTag) });

        return app;
    }
}
