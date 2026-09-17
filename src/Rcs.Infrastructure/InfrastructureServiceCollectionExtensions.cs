using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using Rcs.Application.Concurrency;
using Rcs.Application.Identifiers;
using Rcs.Application.Persistence;
using Rcs.Infrastructure.Concurrency;
using Rcs.Infrastructure.Configuration;
using Rcs.Infrastructure.Identifiers;
using Rcs.Infrastructure.Migrations;
using Rcs.Infrastructure.Persistence;

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

        return services;
    }
}
