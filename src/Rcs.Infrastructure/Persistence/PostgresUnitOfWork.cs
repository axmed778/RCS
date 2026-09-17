using System.Data;
using Npgsql;
using Rcs.Application.Persistence;

namespace Rcs.Infrastructure.Persistence;

/// <summary>
/// One pooled PostgreSQL connection and one READ COMMITTED transaction for one use case
/// (ARCHITECTURE.md §12.1). Disposing without committing rolls back.
/// </summary>
public sealed class PostgresUnitOfWork : IUnitOfWork
{
    private readonly NpgsqlConnection connection;
    private NpgsqlTransaction? transaction;

    private PostgresUnitOfWork(NpgsqlConnection connection, NpgsqlTransaction transaction)
    {
        this.connection = connection;
        this.transaction = transaction;
    }

    public bool IsActive => transaction is not null;

    /// <summary>The connection, for infrastructure code running inside this unit of work.</summary>
    public NpgsqlConnection Connection => IsActive ? connection : throw NotActive();

    /// <summary>The transaction every command of this unit of work must enlist in.</summary>
    public NpgsqlTransaction Transaction => transaction ?? throw NotActive();

    internal static async Task<PostgresUnitOfWork> BeginAsync(NpgsqlDataSource dataSource, CancellationToken cancellationToken)
    {
        var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        try
        {
            var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
            return new PostgresUnitOfWork(connection, transaction);
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    public async Task CommitAsync(CancellationToken cancellationToken = default)
    {
        var current = transaction ?? throw NotActive();
        await current.CommitAsync(cancellationToken);
        transaction = null;
        await current.DisposeAsync();
    }

    public async Task RollbackAsync(CancellationToken cancellationToken = default)
    {
        var current = transaction ?? throw NotActive();
        transaction = null;
        try
        {
            await current.RollbackAsync(cancellationToken);
        }
        finally
        {
            await current.DisposeAsync();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (transaction is not null)
        {
            var current = transaction;
            transaction = null;
            await current.DisposeAsync(); // Npgsql rolls back a transaction that was not completed.
        }

        await connection.DisposeAsync();
    }

    private static InvalidOperationException NotActive() =>
        new("The unit of work has no active transaction: it was already committed, rolled back or disposed.");
}

public sealed class PostgresUnitOfWorkFactory(NpgsqlDataSource dataSource) : IUnitOfWorkFactory
{
    public async Task<IUnitOfWork> BeginAsync(CancellationToken cancellationToken = default) =>
        await PostgresUnitOfWork.BeginAsync(dataSource, cancellationToken);
}
