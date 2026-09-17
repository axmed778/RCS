using Rcs.Application.Persistence;

namespace Rcs.Application.Idempotency;

/// <summary>
/// Durable identity of one retry-sensitive command (DECISIONS.md ADR-020, ARCHITECTURE.md §12.6). It is
/// generated once, when the form or upload is prepared — never per attempt — so every retry of the same
/// submission carries the same value.
/// </summary>
public readonly record struct OperationId
{
    public OperationId(Guid value)
    {
        if (value == Guid.Empty)
        {
            throw new ArgumentException("An operation id cannot be empty.", nameof(value));
        }

        Value = value;
    }

    public Guid Value { get; }

    public override string ToString() => Value.ToString("D");
}

/// <summary>What a completed operation produced, so a retry can return the same result.</summary>
/// <param name="ActorUserId">The actor the operation is bound to. A retry by a different actor is rejected.</param>
/// <param name="OperationKind">A stable command name, e.g. an upload or a correspondence registration.</param>
/// <param name="RecordedAt">
/// When the receipt was written. It is written in the same transaction as the command's effects, so this
/// is also when the operation completed.
/// </param>
/// <param name="ResultReference">A reference to the result the original attempt returned.</param>
public sealed record OperationReceipt(
    OperationId OperationId,
    Guid ActorUserId,
    string OperationKind,
    DateTimeOffset RecordedAt,
    string ResultReference);

/// <summary>
/// Durable operation receipts. <b>Phase 1 defines the contract only; there is no implementation and no
/// table yet.</b> The receipt is bound to a real user (a foreign key to the user table), which does not
/// exist until the identity migration; creating the table earlier would mean a column without its foreign
/// key. ADR-020 needs it before the first retry-sensitive command ships, and that command cannot exist
/// before users do.
/// </summary>
/// <remarks>
/// Usage inside one unit of work: <see cref="FindAsync"/> first; if a receipt exists for the same actor,
/// return its result and execute nothing. Otherwise execute, then <see cref="RecordAsync"/> before commit.
/// Two concurrent attempts with the same id serialise on the receipt's unique key: the loser's insert fails,
/// its transaction rolls back, and it returns the winner's receipt.
/// </remarks>
public interface IOperationReceiptStore
{
    Task<OperationReceipt?> FindAsync(IUnitOfWork unitOfWork, OperationId operationId, CancellationToken cancellationToken = default);

    Task RecordAsync(IUnitOfWork unitOfWork, OperationReceipt receipt, CancellationToken cancellationToken = default);
}
