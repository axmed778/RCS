using Microsoft.Extensions.Logging;
using Npgsql;
using Rcs.Application.Common;
using Rcs.Application.Concurrency;
using Rcs.Application.Identifiers;
using Rcs.Application.Idempotency;
using Rcs.Application.Identity;
using Rcs.Application.Persistence;
using Rcs.Domain.Authorization;
using Rcs.Domain.Vocabulary;
using Rcs.Infrastructure.Audit;
using Rcs.Infrastructure.Identity;
using Rcs.Infrastructure.Persistence;

namespace Rcs.Infrastructure.Commands;

/// <summary>What a refused authorization was about, so the refusal can be audited after the rollback.</summary>
internal sealed record Denial(BusinessAction Action, string Code, string EntityType, Guid EntityId, Guid? CaseId);

/// <summary>The transaction, actor and helpers available to one command body.</summary>
internal sealed class CommandScope(
    PostgresUnitOfWork unitOfWork,
    ActorProfile actor,
    string? clientHost,
    DateTimeOffset now,
    Guid correlationId,
    AuditWriter audit,
    IIdGenerator ids)
{
    public PostgresUnitOfWork UnitOfWork { get; } = unitOfWork;

    public ActorProfile Actor { get; } = actor;

    public ActorAuthority Authority { get; } = actor.Authority();

    public DateTimeOffset Now { get; } = now;

    public Guid CorrelationId { get; } = correlationId;

    public Denial? Denial { get; private set; }

    public Guid NewId() => ids.NewId();

    public Task AuditAsync(AuditEntry entry, CancellationToken cancellationToken) =>
        audit.WriteAsync(UnitOfWork, Actor, clientHost, CorrelationId, entry, cancellationToken);

    /// <summary>Evaluates <c>can()</c>. Returns null when allowed; otherwise the failure to return, remembered for the denial audit event.</summary>
    public CommandResult<Guid>? Refuse(BusinessAction action, CaseRelationship? relationship, string entityType, Guid entityId, Guid? caseId)
    {
        var decision = AuthorizationPolicy.Decide(Authority, action, relationship);
        if (decision.IsAllowed)
        {
            return null;
        }

        Denial = new Denial(action, decision.DenialCode!, entityType, entityId, caseId);
        return CommandResult<Guid>.Failure(CommandErrorKind.Forbidden, decision.DenialCode!);
    }
}

/// <summary>
/// The shape every command shares (ARCHITECTURE.md §12): one transaction; the actor and their roles read inside it;
/// the case serialization lock taken first (ADR-019); the operation receipt checked before and recorded with the
/// effects (ADR-020); audit rows in the same transaction; a refusal rolled back and then recorded as
/// PERMISSION_DENIED. Database constraint violations become rule violations rather than server errors.
/// </summary>
internal sealed class CommandRunner(
    IUnitOfWorkFactory unitOfWorkFactory,
    ICaseSerializationLock caseLock,
    IOperationReceiptStore receipts,
    AuditWriter audit,
    IIdGenerator ids,
    TimeProvider timeProvider,
    ILogger<CommandRunner> logger)
{
    public async Task<CommandResult<Guid>> RunAsync(
        ActorContext actor,
        string operationKind,
        OperationId? operationId,
        Guid? caseId,
        Func<CommandScope, CancellationToken, Task<CommandResult<Guid>>> body,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        try
        {
            return await RunOnceAsync(actor, operationKind, operationId, caseId, body, cancellationToken);
        }
        catch (PostgresException exception) when (exception is { SqlState: PostgresErrorCodes.UniqueViolation, ConstraintName: "operation_receipt_pk" } && operationId is { } id)
        {
            // A concurrent attempt with the same operation id committed first: answer with its result.
            await using var unitOfWork = await unitOfWorkFactory.BeginAsync(cancellationToken);
            var receipt = await receipts.FindAsync(unitOfWork, id, cancellationToken);
            return FromReceipt(receipt, actor.UserId, operationKind);
        }
        catch (PostgresException exception) when (exception.SqlState is PostgresErrorCodes.UniqueViolation or PostgresErrorCodes.ExclusionViolation
                                                      or PostgresErrorCodes.CheckViolation or PostgresErrorCodes.ForeignKeyViolation)
        {
            logger.LogWarning("Command {OperationKind} refused by database constraint {Constraint} ({SqlState}).", operationKind, exception.ConstraintName, exception.SqlState);
            return CommandResult<Guid>.Failure(CommandErrorKind.RuleViolation, $"db.{exception.ConstraintName}");
        }
    }

    private async Task<CommandResult<Guid>> RunOnceAsync(
        ActorContext actor,
        string operationKind,
        OperationId? operationId,
        Guid? caseId,
        Func<CommandScope, CancellationToken, Task<CommandResult<Guid>>> body,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        await using var unitOfWork = (PostgresUnitOfWork)await unitOfWorkFactory.BeginAsync(cancellationToken);

        var profile = await ActorStore.LoadAsync(unitOfWork, actor.UserId, now, cancellationToken);
        if (profile is null)
        {
            return CommandResult<Guid>.Failure(CommandErrorKind.Forbidden, "auth.unknown_actor");
        }

        if (caseId is { } lockedCase)
        {
            await caseLock.AcquireAsync(unitOfWork, lockedCase, cancellationToken);
        }

        if (operationId is { } id && await receipts.FindAsync(unitOfWork, id, cancellationToken) is { } existing)
        {
            return FromReceipt(existing, profile.UserId, operationKind);
        }

        var scope = new CommandScope(unitOfWork, profile, actor.ClientHost, now, ids.NewId(), audit, ids);
        var result = await body(scope, cancellationToken);
        if (!result.Succeeded)
        {
            await unitOfWork.RollbackAsync(cancellationToken);
            if (scope.Denial is { } denial)
            {
                await WriteDenialAsync(profile, actor.ClientHost, denial, cancellationToken);
            }

            return result;
        }

        if (operationId is { } recorded)
        {
            await receipts.RecordAsync(unitOfWork, new OperationReceipt(recorded, profile.UserId, operationKind, now, result.Value.ToString("D")), cancellationToken);
        }

        await unitOfWork.CommitAsync(cancellationToken);
        return result;
    }

    private async Task WriteDenialAsync(ActorProfile actor, string? clientHost, Denial denial, CancellationToken cancellationToken)
    {
        await using var unitOfWork = (PostgresUnitOfWork)await unitOfWorkFactory.BeginAsync(cancellationToken);
        await audit.WriteAsync(unitOfWork, actor, clientHost, ids.NewId(), new AuditEntry(
            AuditActionCodes.PermissionDenied,
            denial.EntityType,
            denial.EntityId,
            EntityVersion: null,
            denial.CaseId,
            After: new { attempted_action = denial.Action.ToString(), denial_code = denial.Code }), cancellationToken);
        await unitOfWork.CommitAsync(cancellationToken);
    }

    private static CommandResult<Guid> FromReceipt(OperationReceipt? receipt, Guid actorUserId, string operationKind) =>
        receipt is not null && receipt.ActorUserId == actorUserId && receipt.OperationKind == operationKind && Guid.TryParse(receipt.ResultReference, out var result)
            ? CommandResult<Guid>.Success(result)
            : CommandResult<Guid>.Failure(CommandErrorKind.Conflict, "operation.already_used");
}
