using Rcs.Application.Idempotency;
using Rcs.Application.Persistence;
using Rcs.Infrastructure.Persistence;

namespace Rcs.Infrastructure.Idempotency;

/// <summary><c>rcs.operation_receipt</c> (migration 0012, ADR-020, ADR-039).</summary>
public sealed class PostgresOperationReceiptStore : IOperationReceiptStore
{
    public async Task<OperationReceipt?> FindAsync(IUnitOfWork unitOfWork, OperationId operationId, CancellationToken cancellationToken = default)
    {
        var postgres = Require(unitOfWork);
        return await postgres.Command("""
                SELECT operation_id, actor_user_id, operation_kind, recorded_at, result_reference
                FROM rcs.operation_receipt WHERE operation_id = @id
                """)
            .With("id", operationId.Value)
            .SingleOrDefaultAsync(
                reader => new OperationReceipt(
                    new OperationId(reader.Uuid("operation_id")),
                    reader.Uuid("actor_user_id"),
                    reader.Text("operation_kind"),
                    reader.Instant("recorded_at"),
                    reader.Text("result_reference")),
                cancellationToken);
    }

    public async Task RecordAsync(IUnitOfWork unitOfWork, OperationReceipt receipt, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        await Require(unitOfWork).Command("""
                INSERT INTO rcs.operation_receipt (operation_id, actor_user_id, operation_kind, result_reference, recorded_at)
                VALUES (@id, @actor, @kind, @result, @recorded_at)
                """)
            .With("id", receipt.OperationId.Value)
            .With("actor", receipt.ActorUserId)
            .With("kind", receipt.OperationKind)
            .With("result", receipt.ResultReference)
            .With("recorded_at", receipt.RecordedAt)
            .ExecuteAsync(cancellationToken);
    }

    private static PostgresUnitOfWork Require(IUnitOfWork unitOfWork) =>
        unitOfWork as PostgresUnitOfWork ?? throw new ArgumentException("A PostgreSQL unit of work is required.", nameof(unitOfWork));
}
