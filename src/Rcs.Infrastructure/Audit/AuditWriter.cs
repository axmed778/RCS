using System.Text.Json;
using Rcs.Application.Identifiers;
using Rcs.Application.Identity;
using Rcs.Domain.Vocabulary;
using Rcs.Infrastructure.Persistence;

namespace Rcs.Infrastructure.Audit;

/// <summary>One audit row to write (DOMAIN_MODEL.md §2.18). States are small projections, not whole rows.</summary>
public sealed record AuditEntry(
    string ActionCode,
    string EntityType,
    Guid EntityId,
    int? EntityVersion,
    Guid? CaseId,
    object? Before = null,
    object? After = null,
    string? ReasonNote = null,
    ActorKind ActorKind = ActorKind.User,
    DateTimeOffset? OccurredAt = null);

/// <summary>
/// Writes audit events inside the business transaction; a failed audit write fails the operation (SECURITY.md §9.1).
/// The runtime role can only INSERT and SELECT this table (ADR-018).
/// </summary>
public sealed class AuditWriter(IIdGenerator ids, TimeProvider timeProvider)
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    /// <summary>Writes an event attributed to <paramref name="actor"/>; SYSTEM consequences are still attributed to the initiating person (A-8).</summary>
    public Task WriteAsync(PostgresUnitOfWork unitOfWork, ActorProfile actor, string? clientHost, Guid correlationId, AuditEntry entry, CancellationToken cancellationToken)
    {
        if (entry.ActorKind == ActorKind.Job)
        {
            throw new ArgumentException("A person's command cannot write a JOB event.", nameof(entry));
        }

        return InsertAsync(unitOfWork, actor, clientHost, correlationId, entry, cancellationToken);
    }

    /// <summary>A background or installation run with no initiating person: actor_kind JOB, no actor.</summary>
    public Task WriteJobAsync(PostgresUnitOfWork unitOfWork, Guid correlationId, AuditEntry entry, CancellationToken cancellationToken) =>
        InsertAsync(unitOfWork, actor: null, clientHost: null, correlationId, entry with { ActorKind = ActorKind.Job }, cancellationToken);

    private async Task InsertAsync(PostgresUnitOfWork unitOfWork, ActorProfile? actor, string? clientHost, Guid correlationId, AuditEntry entry, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        await unitOfWork.Command("""
                INSERT INTO rcs.audit_event (
                    id, occurred_at, actor_user_id, actor_kind, actor_username_snapshot, actor_display_name_snapshot,
                    actor_roles_snapshot, client_host, action_code, entity_type, entity_id, entity_version, case_id,
                    before_state, after_state, reason_note, correlation_id)
                VALUES (
                    @id, @occurred_at, @actor_user_id, @actor_kind, @username, @display_name,
                    @roles, @client_host, @action_code, @entity_type, @entity_id, @entity_version, @case_id,
                    @before_state, @after_state, @reason_note, @correlation_id)
                """)
            .With("id", ids.NewId())
            .With("occurred_at", entry.OccurredAt ?? now)
            .With("actor_user_id", actor?.UserId)
            .With("actor_kind", entry.ActorKind.ToCode())
            .With("username", actor?.Username)
            .With("display_name", actor?.DisplayName)
            .WithTextArray("roles", actor?.Roles.Select(role => role.ToCode()).Order(StringComparer.Ordinal).ToArray())
            .With("client_host", clientHost)
            .With("action_code", entry.ActionCode)
            .With("entity_type", entry.EntityType)
            .With("entity_id", entry.EntityId)
            .With("entity_version", entry.EntityVersion)
            .With("case_id", entry.CaseId)
            .WithJson("before_state", entry.Before is null ? null : JsonSerializer.Serialize(entry.Before, Json))
            .WithJson("after_state", entry.After is null ? null : JsonSerializer.Serialize(entry.After, Json))
            .With("reason_note", entry.ReasonNote)
            .With("correlation_id", correlationId)
            .ExecuteAsync(cancellationToken);
    }
}
