using System.Data.Common;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Rcs.Infrastructure.Migrations;

namespace Rcs.Web.Hosting;

/// <summary>Readiness: the database is reachable with the runtime role and the schema is compatible.</summary>
public sealed class DatabaseSchemaHealthCheck(SchemaCompatibilityChecker checker) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            var report = await checker.CheckAsync(cancellationToken);
            return report.IsCompatible
                ? HealthCheckResult.Healthy()
                : HealthCheckResult.Unhealthy(report.Status.ToString());
        }
        catch (DbException exception)
        {
            return HealthCheckResult.Unhealthy("Database unreachable.", exception);
        }
    }
}
