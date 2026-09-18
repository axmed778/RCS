namespace Rcs.Domain.Vocabulary;

// Closed vocabularies the application branches on. DOMAIN_MODEL.md §1.4 stores each as
// `text` + `CHECK (col IN (...))`; [DbCode] is the exact stored value. Numeric values start at 1 so
// that default(T) is never a valid state, and they are never persisted.
//
// Open vocabularies that clerks classify with (response_type, response_outcome, void_reason,
// document_link_role, ...) are lookup tables, not enums, and do not belong here.

/// <summary><c>case.lifecycle_state</c> — DOMAIN_MODEL.md §2.5, §5.1; DECISIONS.md ADR-029.</summary>
public enum CaseLifecycleState
{
    [DbCode("REGISTERED")] Registered = 1,
    [DbCode("ACTIVE")] Active,
    [DbCode("ON_HOLD")] OnHold,
    [DbCode("CLOSED")] Closed,
    [DbCode("CANCELLED")] Cancelled,
}

/// <summary><c>request.status</c> — DOMAIN_MODEL.md §2.9, §5.2; WORKFLOW.md §3.</summary>
public enum RequestStatus
{
    [DbCode("DRAFT")] Draft = 1,
    [DbCode("SENT")] Sent,
    [DbCode("ANSWERED")] Answered,
    [DbCode("CLOSED")] Closed,
    [DbCode("WITHDRAWN")] Withdrawn,
    [DbCode("VOID")] Void,
}

/// <summary><c>response.status</c> — DOMAIN_MODEL.md §2.10, §2.21.</summary>
public enum ResponseStatus
{
    [DbCode("ACTIVE")] Active = 1,
    [DbCode("SUPERSEDED")] Superseded,
    [DbCode("VOID")] Void,
}

/// <summary><c>requirement.status</c> — DOMAIN_MODEL.md §2.11, §5.4; WORKFLOW.md §5.</summary>
public enum RequirementStatus
{
    [DbCode("OPEN")] Open = 1,
    [DbCode("IN_PROGRESS")] InProgress,
    [DbCode("FULFILLED")] Fulfilled,
    [DbCode("WAIVED")] Waived,
    [DbCode("VOID")] Void,
    [DbCode("FAILED")] Failed,
}

/// <summary><c>document_version.status</c> — DOMAIN_MODEL.md §2.14, §5.6; DECISIONS.md ADR-027.</summary>
public enum DocumentVersionStatus
{
    [DbCode("ACTIVE")] Active = 1,
    [DbCode("SUPERSEDED")] Superseded,
    [DbCode("WITHDRAWN")] Withdrawn,
}

/// <summary><c>final_result.status</c> — DOMAIN_MODEL.md §2.16; WORKFLOW.md §8.</summary>
public enum FinalResultStatus
{
    [DbCode("DRAFT")] Draft = 1,
    [DbCode("ISSUED")] Issued,
    [DbCode("SUPERSEDED")] Superseded,
    [DbCode("REVOKED")] Revoked,
    [DbCode("VOID")] Void,
}

/// <summary><c>correspondence.status</c> — DOMAIN_MODEL.md §2.8, §5.5.</summary>
public enum CorrespondenceStatus
{
    [DbCode("DRAFT")] Draft = 1,
    [DbCode("REGISTERED")] Registered,
    [DbCode("SENT")] Sent,
    [DbCode("RECEIVED")] Received,
    [DbCode("WITHDRAWN")] Withdrawn,
    [DbCode("SUPERSEDED")] Superseded,
    [DbCode("VOID")] Void,
}

/// <summary><c>correspondence.direction</c> — DOMAIN_MODEL.md §2.8.</summary>
public enum CorrespondenceDirection
{
    [DbCode("IN")] Incoming = 1,
    [DbCode("OUT")] Outgoing,
}

/// <summary><c>assignment.status</c> — DOMAIN_MODEL.md §2.7, §5.7; DECISIONS.md ADR-017.</summary>
public enum AssignmentStatus
{
    [DbCode("ACTIVE")] Active = 1,
    [DbCode("ENDED")] Ended,
    [DbCode("VOID")] Void,
}

/// <summary><c>user.status</c> (table <c>app_user</c>) — DOMAIN_MODEL.md §2.3; PERMISSIONS.md §25.</summary>
public enum UserStatus
{
    [DbCode("ACTIVE")] Active = 1,
    [DbCode("SUSPENDED")] Suspended,
    [DbCode("DEACTIVATED")] Deactivated,
}

/// <summary><c>role.code</c> — the four conceptual roles (DOMAIN_MODEL.md §2.4; PERMISSIONS.md §15–§18).</summary>
public enum BusinessRole
{
    [DbCode("WORKER")] Worker = 1,
    [DbCode("CHIEF")] Chief,
    [DbCode("HEAD")] Head,
    [DbCode("TECH_ADMIN")] TechAdmin,
}

/// <summary>
/// <c>audit_event.actor_kind</c> — how a change was executed (DOMAIN_MODEL.md §2.18, amendment A-8).
/// <see cref="User"/>: the person's own act. <see cref="System"/>: a mechanical consequence executed
/// inside a person's command, still attributed to that person. <see cref="Job"/>: a background run with
/// no initiating person — the only kind whose actor user is empty.
/// </summary>
public enum ActorKind
{
    [DbCode("USER")] User = 1,
    [DbCode("SYSTEM")] System,
    [DbCode("JOB")] Job,
}
