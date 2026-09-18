using Rcs.Application.Common;
using Rcs.Application.Idempotency;

namespace Rcs.Application.Workflow;

/// <summary>
/// Registers an outgoing official letter that was ALREADY SENT in the external government system, together with the
/// request it carried (WORKFLOW.md §3.2 R1b). The request is SENT from birth because the registered letter is the
/// proof of issuance; there is no send action. A letter number already registered in the same case for the same
/// recipient and date is the same letter: the request joins it (decision C-1, one letter carrying several requests).
/// </summary>
/// <param name="SourceRequirementId">Set for a child request created to satisfy that requirement; write-once.</param>
public sealed record RegisterRequestCommand(
    OperationId OperationId,
    Guid CaseId,
    Guid TargetOrganizationId,
    string Subject,
    string OutgoingLetterNumber,
    DateOnly SentDate,
    DateOnly? DueDate,
    Guid? SourceRequirementId,
    string? RequestedItemsNote);

/// <summary>
/// Registers an incoming letter ALREADY RECEIVED externally as a response to one request (WORKFLOW.md §4.1). Type and
/// outcome are separate axes (decision C-2); conclusiveness is the clerk's judgement. ANSWERED follows mechanically.
/// </summary>
public sealed record RegisterResponseCommand(
    OperationId OperationId,
    Guid CaseId,
    Guid RequestId,
    string IncomingLetterNumber,
    string? IncomingRegistryNumber,
    DateOnly LetterDate,
    DateOnly? ReceivedDate,
    string ResponseTypeCode,
    string ResponseOutcomeCode,
    bool IsConclusive,
    string? Summary);

/// <summary>A condition imposed by a response (WORKFLOW.md §4.4, §5.1): origin RESPONSE, created OPEN.</summary>
public sealed record CreateRequirementCommand(
    OperationId OperationId,
    Guid CaseId,
    Guid SourceResponseId,
    string Title,
    string? Description,
    bool IsBlocking,
    DateOnly? DueDate,
    Guid? AddressedToOrganizationId);

public sealed record StartRequirementCommand(Guid CaseId, Guid RequirementId, int RowVersion);

/// <summary>Q3. Evidence is a response (typically the child request's answer), or an explicit resolution note, or both.</summary>
public sealed record FulfillRequirementCommand(Guid CaseId, Guid RequirementId, int RowVersion, Guid? EvidenceResponseId, string? ResolutionNote);

/// <summary>Q4 — Chief or Head; the obligation still applied and the case is released from it.</summary>
public sealed record WaiveRequirementCommand(Guid CaseId, Guid RequirementId, int RowVersion, string WaiverReasonCode, string Note);

/// <summary>Q5 — the obligation no longer applies (or never did). <paramref name="VoidSourceResponseId"/> names the later response that removed the need.</summary>
public sealed record VoidRequirementCommand(Guid CaseId, Guid RequirementId, int RowVersion, string VoidReasonCode, string Note, Guid? VoidSourceResponseId);

/// <summary>Q6 — Chief or Head; the obligation applied, was not released, and could not be satisfied.</summary>
public sealed record FailRequirementCommand(Guid CaseId, Guid RequirementId, int RowVersion, string FailureReasonNote);

/// <summary>R4 — always a person's act, under the frozen closure rule (WORKFLOW.md §3.4).</summary>
public sealed record CloseRequestCommand(Guid CaseId, Guid RequestId, int RowVersion, string? Note);

public interface IWorkflowService
{
    Task<CommandResult<Guid>> RegisterRequestAsync(ActorContext actor, RegisterRequestCommand command, CancellationToken cancellationToken = default);

    Task<CommandResult<Guid>> RegisterResponseAsync(ActorContext actor, RegisterResponseCommand command, CancellationToken cancellationToken = default);

    Task<CommandResult<Guid>> CreateRequirementAsync(ActorContext actor, CreateRequirementCommand command, CancellationToken cancellationToken = default);

    Task<CommandResult<Guid>> StartRequirementAsync(ActorContext actor, StartRequirementCommand command, CancellationToken cancellationToken = default);

    Task<CommandResult<Guid>> FulfillRequirementAsync(ActorContext actor, FulfillRequirementCommand command, CancellationToken cancellationToken = default);

    Task<CommandResult<Guid>> WaiveRequirementAsync(ActorContext actor, WaiveRequirementCommand command, CancellationToken cancellationToken = default);

    Task<CommandResult<Guid>> VoidRequirementAsync(ActorContext actor, VoidRequirementCommand command, CancellationToken cancellationToken = default);

    Task<CommandResult<Guid>> FailRequirementAsync(ActorContext actor, FailRequirementCommand command, CancellationToken cancellationToken = default);

    Task<CommandResult<Guid>> CloseRequestAsync(ActorContext actor, CloseRequestCommand command, CancellationToken cancellationToken = default);
}
