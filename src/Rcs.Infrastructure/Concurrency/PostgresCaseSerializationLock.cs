using Npgsql;
using Rcs.Application.Concurrency;
using Rcs.Application.Persistence;
using Rcs.Infrastructure.Persistence;

namespace Rcs.Infrastructure.Concurrency;

/// <summary>
/// ADR-019 implemented with <c>pg_advisory_xact_lock</c>: a transaction-scoped advisory lock per case,
/// acquired in ascending key order. The lock ends with the transaction, so it can never leak into the
/// connection pool. An in-process lock would not be authoritative and is deliberately not used.
/// </summary>
public sealed class PostgresCaseSerializationLock : ICaseSerializationLock
{
    public async Task AcquireAsync(IUnitOfWork unitOfWork, IReadOnlyCollection<Guid> caseIds, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(unitOfWork);
        ArgumentNullException.ThrowIfNull(caseIds);

        if (unitOfWork is not PostgresUnitOfWork postgres)
        {
            throw new ArgumentException("The case serialization lock requires a PostgreSQL unit of work.", nameof(unitOfWork));
        }

        if (!postgres.IsActive)
        {
            throw new InvalidOperationException(
                "Case serialization locks are transaction-scoped; the unit of work has no active transaction.");
        }

        if (caseIds.Count == 0)
        {
            throw new ArgumentException("At least one case id is required.", nameof(caseIds));
        }

        foreach (var key in CaseLockKey.DeriveOrdered(caseIds))
        {
            await using var command = new NpgsqlCommand("SELECT pg_advisory_xact_lock($1)", postgres.Connection, postgres.Transaction);
            command.Parameters.Add(new NpgsqlParameter<long> { TypedValue = key });
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }
}
