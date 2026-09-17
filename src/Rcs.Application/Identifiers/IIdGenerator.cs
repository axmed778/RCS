namespace Rcs.Application.Identifiers;

/// <summary>
/// The only source of new entity identifiers: time-ordered UUIDv7 (DOMAIN_MODEL.md §1.4, DECISIONS.md
/// ADR-031). Do not call <c>Guid.NewGuid</c> for identifiers — a repository test enforces it.
/// </summary>
/// <remarks>
/// UUIDv7 gives index locality, not an ordering authority: identifiers created in the same millisecond
/// have no defined order. Audit ordering uses <c>audit_event.event_seq</c>.
/// </remarks>
public interface IIdGenerator
{
    Guid NewId();
}
