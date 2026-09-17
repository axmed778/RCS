namespace Rcs.Application.Persistence;

/// <summary>
/// One use case, one database transaction (ARCHITECTURE.md §12.1). The business change and its audit
/// rows commit together or not at all. Disposing without committing rolls back.
/// </summary>
public interface IUnitOfWork : IAsyncDisposable
{
    /// <summary>True until the transaction is committed, rolled back or disposed.</summary>
    bool IsActive { get; }

    Task CommitAsync(CancellationToken cancellationToken = default);

    Task RollbackAsync(CancellationToken cancellationToken = default);
}

public interface IUnitOfWorkFactory
{
    Task<IUnitOfWork> BeginAsync(CancellationToken cancellationToken = default);
}
