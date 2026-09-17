using Rcs.Application.Persistence;

namespace Rcs.Application.Concurrency;

/// <summary>
/// The single case-level serialization convention of DECISIONS.md ADR-019 / ARCHITECTURE.md §12.5.
/// </summary>
/// <remarks>
/// Every operation that evaluates a case-scoped cross-row guard (closure, override, cancellation,
/// reopening, final-result issue, request closure, response supersession and its retraction) and every
/// operation that adds, removes or changes closure-relevant work MUST call this first, inside its unit
/// of work, before reading anything the guard depends on. Row-version checks alone cannot prevent
/// write skew across rows.
/// <para>
/// The lock is transaction-scoped: it is released when the unit of work commits, rolls back or is
/// disposed, never earlier and never later. Several cases are locked in one deterministic order, so two
/// operations touching the same cases cannot deadlock on this lock.
/// </para>
/// </remarks>
public interface ICaseSerializationLock
{
    Task AcquireAsync(IUnitOfWork unitOfWork, IReadOnlyCollection<Guid> caseIds, CancellationToken cancellationToken = default);
}

public static class CaseSerializationLockExtensions
{
    public static Task AcquireAsync(
        this ICaseSerializationLock caseLock,
        IUnitOfWork unitOfWork,
        Guid caseId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(caseLock);
        return caseLock.AcquireAsync(unitOfWork, [caseId], cancellationToken);
    }
}
