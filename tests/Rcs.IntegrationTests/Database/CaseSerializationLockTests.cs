using Npgsql;
using Rcs.Application.Concurrency;
using Rcs.Application.Persistence;
using Rcs.Infrastructure.Concurrency;
using Rcs.Infrastructure.Identifiers;
using Rcs.Infrastructure.Persistence;
using Rcs.IntegrationTests.TestSupport;

namespace Rcs.IntegrationTests.Database;

public sealed class CaseSerializationLockTests : IAsyncLifetime
{
    private static readonly Guid CaseA = Guid.Parse("01923b6e-5c7a-7d1e-9a4b-3c2d1e0f9a8b");
    private static readonly Guid CaseB = Guid.Parse("01923b6e-5c7a-7d1e-9a4b-3c2d1e0f9a8c");

    private readonly PostgresCaseSerializationLock caseLock = new();
    private TestDatabase database = null!;
    private NpgsqlDataSource dataSource = null!;
    private PostgresUnitOfWorkFactory unitOfWorkFactory = null!;

    public async ValueTask InitializeAsync()
    {
        database = await TestDatabase.CreateAsync();
        await database.MigrateAsync();
        dataSource = NpgsqlDataSource.Create(database.RuntimeConnectionString);
        unitOfWorkFactory = new PostgresUnitOfWorkFactory(dataSource);
    }

    public async ValueTask DisposeAsync()
    {
        await dataSource.DisposeAsync();
        await database.DisposeAsync();
    }

    /// <summary>Tries the lock from a separate session without waiting, and releases it at once if it was free.</summary>
    private async Task<bool> IsFreeElsewhereAsync(Guid caseId)
    {
        await using var connection = new NpgsqlConnection(database.AdminConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("SELECT pg_try_advisory_xact_lock($1)", connection);
        command.Parameters.Add(new NpgsqlParameter<long> { TypedValue = CaseLockKey.Derive(caseId) });
        return await command.ExecuteScalarAsync() is true; // autocommit: released when the statement ends
    }

    [Fact]
    public async Task LockBlocksOtherTransactionsUntilCommit()
    {
        await using (var unitOfWork = await unitOfWorkFactory.BeginAsync())
        {
            await caseLock.AcquireAsync(unitOfWork, CaseA);
            Assert.False(await IsFreeElsewhereAsync(CaseA));
            Assert.True(await IsFreeElsewhereAsync(CaseB)); // other cases are unaffected

            await unitOfWork.CommitAsync();
            Assert.True(await IsFreeElsewhereAsync(CaseA));
        }
    }

    [Fact]
    public async Task LockIsReleasedByRollbackAndByDisposeWithoutCommit()
    {
        await using (var rolledBack = await unitOfWorkFactory.BeginAsync())
        {
            await caseLock.AcquireAsync(rolledBack, CaseA);
            await rolledBack.RollbackAsync();
        }

        Assert.True(await IsFreeElsewhereAsync(CaseA));

        await using (var abandoned = await unitOfWorkFactory.BeginAsync())
        {
            await caseLock.AcquireAsync(abandoned, CaseA);
        }

        Assert.True(await IsFreeElsewhereAsync(CaseA));
    }

    [Fact]
    public async Task NoAdvisoryLockSurvivesIntoThePooledConnection()
    {
        await using (var first = await unitOfWorkFactory.BeginAsync())
        {
            await caseLock.AcquireAsync(first, [CaseA, CaseB]);
            await first.CommitAsync();
        }

        // The pool hands the same physical connection back; it must hold no advisory lock.
        await using var second = (PostgresUnitOfWork)await unitOfWorkFactory.BeginAsync();
        await using var command = new NpgsqlCommand(
            "SELECT count(*) FROM pg_locks WHERE locktype = 'advisory' AND pid = pg_backend_pid()", second.Connection, second.Transaction);
        Assert.Equal(0L, (long)(await command.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task LockRequiresAnActiveTransaction()
    {
        IUnitOfWork unitOfWork = await unitOfWorkFactory.BeginAsync();
        await unitOfWork.CommitAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() => caseLock.AcquireAsync(unitOfWork, CaseA));
        await unitOfWork.DisposeAsync();
    }

    [Fact]
    public async Task OppositeRequestOrdersSerialiseWithoutDeadlock()
    {
        var firstHolds = new TaskCompletionSource();

        var first = Task.Run(async () =>
        {
            await using var unitOfWork = await unitOfWorkFactory.BeginAsync();
            await caseLock.AcquireAsync(unitOfWork, [CaseA, CaseB]);
            firstHolds.SetResult();
            await Task.Delay(500);
            await unitOfWork.CommitAsync();
        });

        var second = Task.Run(async () =>
        {
            await firstHolds.Task;
            await using var unitOfWork = await unitOfWorkFactory.BeginAsync();
            await caseLock.AcquireAsync(unitOfWork, [CaseB, CaseA]); // waits for the first, never deadlocks
            await unitOfWork.CommitAsync();
        });

        var both = Task.WhenAll(first, second);
        Assert.Same(both, await Task.WhenAny(both, Task.Delay(TimeSpan.FromSeconds(15))));
        await both; // surfaces any PostgresException, e.g. 40P01 deadlock_detected
    }

    [Fact]
    public async Task GeneratedUuidV7IsStoredByPostgresWithItsVersionAndTimestamp()
    {
        var instant = new DateTimeOffset(2026, 9, 17, 10, 30, 15, 123, TimeSpan.Zero);
        var id = new UuidV7IdGenerator(new FixedTimeProvider(instant)).NewId();

        await using var connection = await dataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand("SELECT uuid_extract_version($1), uuid_extract_timestamp($1)", connection);
        command.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = id });
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());

        Assert.Equal(7, reader.GetInt16(0));
        Assert.Equal(instant.UtcDateTime, reader.GetDateTime(1));
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
