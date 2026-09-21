using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Npgsql;
using Rcs.Application.Cases;
using Rcs.Application.Common;
using Rcs.Application.Concurrency;
using Rcs.Application.Identifiers;
using Rcs.Application.Identity;
using Rcs.Application.Idempotency;
using Rcs.Application.Lifecycle;
using Rcs.Application.Lookups;
using Rcs.Application.Organizations;
using Rcs.Application.Persistence;
using Rcs.Application.Workflow;
using Rcs.Domain.Progress;
using Rcs.Infrastructure.Audit;
using Rcs.Infrastructure.Cases;
using Rcs.Infrastructure.Commands;
using Rcs.Infrastructure.Concurrency;
using Rcs.Infrastructure.Configuration;
using Rcs.Infrastructure.Development;
using Rcs.Infrastructure.Documents;
using Rcs.Application.Documents;
using Rcs.Infrastructure.Identifiers;
using Rcs.Infrastructure.Identity;
using Rcs.Infrastructure.Idempotency;
using Rcs.Infrastructure.Lifecycle;
using Rcs.Infrastructure.Lookups;
using Rcs.Infrastructure.Migrations;
using Rcs.Infrastructure.Organizations;
using Rcs.Infrastructure.Persistence;
using Rcs.Infrastructure.Workflow;

namespace Rcs.Infrastructure;

public static class InfrastructureServiceCollectionExtensions
{
    /// <summary>
    /// Registers the runtime infrastructure. Only the <see cref="ConnectionStringNames.Runtime"/> connection is
    /// used here; the migration credentials are never registered in the running application.
    /// </summary>
    public static IServiceCollection AddRcsInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<DatabaseOptions>()
            .Bind(configuration.GetSection(DatabaseOptions.SectionName))
            .Validate(options => options.ExpectedSchemaVersion > 0, "Rcs:Database:ExpectedSchemaVersion must be a positive migration number.")
            .ValidateOnStart();
        services.AddOptions<StorageOptions>()
            .Bind(configuration.GetSection(StorageOptions.SectionName));
        services.AddOptions<LocalAuthenticationOptions>()
            .Bind(configuration.GetSection(LocalAuthenticationOptions.SectionName))
            .Validate(options => options.Argon2MemoryKibibytes is >= 8192 and <= 1048576 && options.Argon2Iterations is >= 2 and <= 16 && options.Argon2Parallelism is >= 1 and <= 16,
                "Rcs:Authentication Argon2id cost is below the minimum this release accepts (m>=8192 KiB, t>=2, p>=1).")
            .Validate(options => options.MaxFailedAttempts is >= 3 and <= 50 && options.LockoutMinutes is >= 1 and <= 60,
                "Rcs:Authentication lockout settings are outside the supported range.")
            .Validate(options => options.SessionLifetimeHours is >= 1 and <= 24 && options.IdleTimeoutMinutes is >= 1 and <= 720
                && options.TemporaryCredentialHours is >= 1 and <= 72 && options.AttemptsPerMinutePerHost is >= 1 and <= 100,
                "Rcs:Authentication session and rate limits are outside the supported range.")
            .ValidateOnStart();

        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<IIdGenerator, UuidV7IdGenerator>();

        services.AddSingleton(_ =>
        {
            var connectionString = configuration.GetConnectionString(ConnectionStringNames.Runtime);
            if (string.IsNullOrWhiteSpace(connectionString))
            {
                throw new InvalidOperationException(
                    "ConnectionStrings:Runtime is not configured. Provide the rcs_app connection string through RCS_SECRETS_FILE or the ConnectionStrings__Runtime environment variable (README.md).");
            }

            return NpgsqlDataSource.Create(connectionString);
        });

        services.AddSingleton<IUnitOfWorkFactory, PostgresUnitOfWorkFactory>();
        services.AddSingleton<ICaseSerializationLock, PostgresCaseSerializationLock>();
        services.AddSingleton(_ => MigrationSet.LoadEmbedded());
        services.AddSingleton<SchemaCompatibilityChecker>();

        // Business calendar: dates on letters are read and written in the department's time zone.
        services.AddOptions<BusinessOptions>().Bind(configuration.GetSection(BusinessOptions.SectionName));
        services.AddSingleton(provider =>
        {
            var options = provider.GetRequiredService<IOptions<BusinessOptions>>().Value;
            return new BusinessCalendar(ResolveTimeZone(options.TimeZone), provider.GetRequiredService<TimeProvider>());
        });
        services.AddSingleton(provider => new ProgressEvaluator(provider.GetRequiredService<BusinessCalendar>().TimeZone));

        // Command plumbing (transaction, actor, case lock, receipts, audit).
        services.AddSingleton<IOperationReceiptStore, PostgresOperationReceiptStore>();
        services.AddSingleton<AuditWriter>();
        services.AddSingleton<CommandRunner>();

        // Local authentication: Argon2id credentials and server-side revocable sessions (ADR-033).
        services.AddSingleton<IPasswordHasher, Argon2PasswordHasher>();
        services.AddSingleton<ILocalAuthenticationService, PostgresAuthenticationService>();
        services.AddSingleton<IUserAdministration, PostgresUserAdministration>();
        services.AddSingleton<IdentityBootstrap>();
        services.AddSingleton<Rcs.Infrastructure.Organizations.DepartmentBootstrap>();

        // Modules.
        services.AddSingleton<IUserDirectory, PostgresUserDirectory>();
        services.AddSingleton<ILookupQueries, PostgresLookupQueries>();
        services.AddSingleton<IOrganizationService, PostgresOrganizationService>();
        services.AddSingleton<ICaseService, PostgresCaseService>();
        services.AddSingleton<ICaseQueries, PostgresCaseQueries>();
        services.AddSingleton<IWorkflowService, PostgresWorkflowService>();
        services.AddSingleton<ICaseLifecycleService, PostgresCaseLifecycleService>();
        services.AddSingleton<ILifecycleQueries, PostgresLifecycleQueries>();

        // Documents: the local content-addressed store and the services over it (DOCUMENT_MODEL.md; ADR-005).
        services.AddSingleton<LocalContentStore>();
        services.AddSingleton<IDocumentService, PostgresDocumentService>();
        services.AddSingleton<IDocumentQueries, PostgresDocumentQueries>();
        services.AddSingleton<IDocumentIntegrityService, DocumentIntegrityService>();
        services.AddSingleton<DemoDataSeeder>();
        services.AddOptions<Rcs.Infrastructure.Previews.PreviewOptions>()
            .Bind(configuration.GetSection(Rcs.Infrastructure.Previews.PreviewOptions.SectionName))
            .Validate(o => o.WorkerConcurrency is >= 1 and <= 4 && o.MaxAttempts is >= 1 and <= 10 && o.JobTimeoutSeconds is >= 5 and <= 1800, "Invalid preview execution limits.")
            .Validate(o => !o.AllowMacroEnabledOffice, "Macro-enabled Office previews are excluded by ADR-042.");
        services.AddSingleton<Rcs.Infrastructure.Previews.PreviewPaths>();
        services.AddSingleton<Rcs.Infrastructure.Previews.PreviewSandbox>();
        services.AddSingleton<Rcs.Infrastructure.Previews.PreviewCapabilityRegistry>();
        services.AddSingleton<Rcs.Infrastructure.Previews.PreviewPlanner>();
        services.AddSingleton<Rcs.Application.Previews.IPreviewJobRunner, Rcs.Infrastructure.Previews.PreviewJobRunner>();
        services.AddSingleton<Rcs.Application.Previews.IDocumentPreviewQueries, Rcs.Infrastructure.Previews.PostgresPreviewQueries>();
        services.AddSingleton<Rcs.Application.Previews.IPreviewAdministration, Rcs.Infrastructure.Previews.PostgresPreviewAdministration>();

        return services;
    }

    /// <summary>
    /// Falls back to a fixed UTC+4 zone when the host has no time-zone database entry, so the application still runs on
    /// a minimal server image. Offline-safe: no lookup leaves the machine.
    /// </summary>
    private static TimeZoneInfo ResolveTimeZone(string id)
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(id);
        }
        catch (Exception exception) when (exception is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            return TimeZoneInfo.CreateCustomTimeZone(id, TimeSpan.FromHours(4), id, id);
        }
    }
}
