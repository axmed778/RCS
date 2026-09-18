using Rcs.Application.Common;
using Rcs.Application.Idempotency;
using Rcs.Domain.Authorization;
using Rcs.Domain.Progress;
using Rcs.Domain.Vocabulary;

namespace Rcs.Application.Cases;

public sealed record OrganizationRef(Guid Id, string Name, string OfficialName);

public sealed record UserRef(Guid Id, string DisplayName);

/// <summary>
/// Registration of an official request already received in the external government system, and the case it opens
/// (WORKFLOW.md §2.1). Nothing is received by this application; the letter's numbers and dates are recorded.
/// </summary>
/// <param name="IncomingLetterNumber">The number printed on the incoming letter (<c>correspondence.letter_number</c>).</param>
/// <param name="IncomingRegistryNumber">The registration number the external registry assigned, if known.</param>
/// <param name="ReceivedDate">When the letter was actually received there; defaults to the letter date.</param>
/// <param name="ResponsibleUserId">Assigning a responsible employee is a Chief action (PERMISSIONS.md §16).</param>
public sealed record CreateCaseCommand(
    OperationId OperationId,
    Guid RequestingOrganizationId,
    string Title,
    string? Subject,
    string IncomingLetterNumber,
    string? IncomingRegistryNumber,
    DateOnly LetterDate,
    DateOnly? ReceivedDate,
    Guid? ResponsibleUserId,
    string? Notes);

public interface ICaseService
{
    Task<CommandResult<Guid>> CreateAsync(ActorContext actor, CreateCaseCommand command, CancellationToken cancellationToken = default);
}

public sealed record CaseListItem(
    Guid Id,
    string CaseNumber,
    string Title,
    OrganizationRef RequestingOrganization,
    UserRef? Responsible,
    CaseLifecycleState State,
    DateTimeOffset RegisteredAt,
    string? IncomingLetterNumber,
    DateOnly? IncomingLetterDate,
    CaseProgress Progress);

/// <summary>
/// One outgoing request the department is still waiting on, for the dashboard reminder (WORKFLOW.md §12.2,
/// §12.5). The reminder is a read of the same derived progress the workspace shows — nothing is stored, and no
/// notification leaves the application.
/// </summary>
/// <param name="CaseIsOnHold">
/// Shown alongside an overdue count rather than suppressing it: a hold is a statement about the department's
/// work, not about the authority's clock (§12.5).
/// </param>
public sealed record DeadlineReminder(
    Guid CaseId,
    string CaseNumber,
    string CaseTitle,
    Guid RequestId,
    string RequestNumber,
    string TargetOrganizationName,
    DateTimeOffset? DueAt,
    RequestProgress Progress,
    bool CaseIsOnHold);

public sealed record DashboardSummary(
    int OpenCases,
    int WaitingForExternalResponse,
    int OpenRequirements,
    int OverdueItems,
    int ReadyForFinalResult,
    IReadOnlyList<CaseListItem> RecentCases,
    IReadOnlyList<DeadlineReminder> AwaitingResponse);

/// <summary>An official letter as recorded here: numbers and dates of an act that happened externally.</summary>
public sealed record LetterView(
    Guid Id,
    CorrespondenceDirection Direction,
    string? LetterNumber,
    string? RegistryNumber,
    DateOnly? LetterDate,
    DateTimeOffset? SentAt,
    DateTimeOffset? ReceivedAt,
    string? Subject,
    OrganizationRef Sender,
    OrganizationRef Recipient,
    DateTimeOffset RegisteredAt,
    UserRef RegisteredBy);

public sealed record CaseHeader(
    Guid Id,
    string CaseNumber,
    string Title,
    string? Subject,
    OrganizationRef RequestingOrganization,
    UserRef? Responsible,
    CaseLifecycleState State,
    DateTimeOffset RegisteredAt,
    DateTimeOffset CreatedAt,
    UserRef CreatedBy,
    string? Notes,
    bool IsRestricted,
    int RowVersion);

public sealed record RequestNode(
    Guid Id,
    string RequestNumber,
    OrganizationRef Target,
    string Subject,
    string? RequestedItemsNote,
    RequestStatus Status,
    int RowVersion,
    LetterView? DispatchLetter,
    DateTimeOffset? DueAt,
    Guid? SourceRequirementId,
    DateTimeOffset? ClosedAt,
    IReadOnlyList<ResponseNode> Responses);

/// <param name="Letter">
/// The carrying letter, or null when it is filed in another case: that letter stays governed by its owning case and
/// is not disclosed here (DOMAIN_MODEL.md §2.8, amendment A-6).
/// </param>
public sealed record ResponseNode(
    Guid Id,
    Guid RequestId,
    LetterView? Letter,
    string TypeCode,
    string OutcomeCode,
    bool IsConclusive,
    string? Summary,
    ResponseStatus Status,
    DateOnly? ResponseDate,
    DateTimeOffset? ReceivedAt,
    DateTimeOffset RecordedAt,
    UserRef RecordedBy,
    IReadOnlyList<RequirementNode> Requirements);

/// <summary>
/// ACTIVE evidence of a requirement: a response, or one exact document version (pinned — DOMAIN_MODEL.md §2.12). The
/// document itself is shown through its REQUIREMENT_EVIDENCE placement, which carries the same pin.
/// </summary>
public sealed record EvidenceView(
    Guid Id,
    Guid? ResponseId,
    string? Note,
    DateTimeOffset RecordedAt,
    UserRef RecordedBy,
    Guid? DocumentId = null,
    Guid? DocumentVersionId = null,
    int RowVersion = 1);

public sealed record RequirementNode(
    Guid Id,
    Guid? SourceResponseId,
    Guid? SourceRequestId,
    string Title,
    string? Description,
    bool IsBlocking,
    RequirementStatus Status,
    int RowVersion,
    DateTimeOffset RaisedAt,
    DateTimeOffset? DueAt,
    OrganizationRef? RaisedBy,
    OrganizationRef? AddressedTo,
    DateTimeOffset? StartedAt,
    DateTimeOffset? ResolvedAt,
    UserRef? ResolvedBy,
    string? ResolutionNote,
    string? WaiverReasonCode,
    string? VoidReasonCode,
    Guid? VoidSourceResponseId,
    string? FailureReasonNote,
    Guid CreatedByUserId,
    IReadOnlyList<EvidenceView> Evidence,
    IReadOnlyList<RequestNode> ChildRequests);

/// <summary>One audit event of the case, for the chronological activity list (PROJECT.md §17).</summary>
public sealed record ActivityEntry(
    long Sequence,
    DateTimeOffset RecordedAt,
    string? ActorDisplayName,
    ActorKind ActorKind,
    string ActionCode,
    string EntityType,
    Guid EntityId,
    string? Detail,
    string? Status,
    string? ReasonNote);

/// <summary>
/// Everything the case workspace shows, in one read. <see cref="Actor"/> and <see cref="ActorIsAssigned"/> let the page
/// ask the same authorization policy the commands use, purely to decide what to render — every command re-checks.
/// </summary>
public sealed record CaseWorkspace(
    CaseHeader Header,
    LetterView? InitiatingLetter,
    IReadOnlyList<RequestNode> TopLevelRequests,
    IReadOnlyList<ResponseNode> AllResponses,
    CaseProgress Progress,
    IReadOnlyList<ActivityEntry> Activity,
    ActorAuthority Actor,
    bool ActorIsAssigned)
{
    public IEnumerable<RequestNode> AllRequests => Flatten(TopLevelRequests);

    public IEnumerable<RequirementNode> AllRequirements => AllRequests.SelectMany(r => r.Responses).SelectMany(p => p.Requirements);

    public RequestNode? FindRequest(Guid id) => AllRequests.FirstOrDefault(r => r.Id == id);

    public ResponseNode? FindResponse(Guid id) => AllResponses.FirstOrDefault(p => p.Id == id);

    public RequirementNode? FindRequirement(Guid id) => AllRequirements.FirstOrDefault(q => q.Id == id);

    public CaseRelationship Relationship => new(Header.IsRestricted, ActorIsAssigned);

    private static IEnumerable<RequestNode> Flatten(IEnumerable<RequestNode> requests)
    {
        foreach (var request in requests)
        {
            yield return request;
            foreach (var child in Flatten(request.Responses.SelectMany(p => p.Requirements).SelectMany(q => q.ChildRequests)))
            {
                yield return child;
            }
        }
    }
}

public interface ICaseQueries
{
    /// <summary>Cases visible to the actor, newest first. Restricted cases the actor cannot see are absent, not counted.</summary>
    Task<IReadOnlyList<CaseListItem>> ListAsync(ActorContext actor, CancellationToken cancellationToken = default);

    Task<DashboardSummary> GetDashboardAsync(ActorContext actor, CancellationToken cancellationToken = default);

    Task<CommandResult<CaseWorkspace>> GetWorkspaceAsync(ActorContext actor, Guid caseId, CancellationToken cancellationToken = default);
}
