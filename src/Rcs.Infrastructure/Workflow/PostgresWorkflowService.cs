using Rcs.Application.Cases;
using Rcs.Application.Common;
using Rcs.Application.Lookups;
using Rcs.Application.Workflow;
using Rcs.Domain.Authorization;
using Rcs.Domain.Vocabulary;
using Rcs.Domain.Workflow;
using Rcs.Infrastructure.Audit;
using Rcs.Infrastructure.Cases;
using Rcs.Infrastructure.Commands;
using Rcs.Infrastructure.Lookups;
using Rcs.Infrastructure.Persistence;
using static Rcs.Infrastructure.Organizations.PostgresOrganizationService;

namespace Rcs.Infrastructure.Workflow;

/// <summary>
/// The workflow spine: registering already-issued requests and already-received responses, raising requirements from
/// responses, resolving them, and closing requests. Every command takes the case lock first (ADR-019), applies the
/// frozen guards, and writes its audit rows in the same transaction.
/// </summary>
internal sealed class PostgresWorkflowService(CommandRunner runner, BusinessCalendar calendar) : IWorkflowService
{
    // ---------------------------------------------------------------- requests

    public Task<CommandResult<Guid>> RegisterRequestAsync(ActorContext actor, RegisterRequestCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        return runner.RunAsync(actor, "request.register", command.OperationId, command.CaseId, async (scope, ct) =>
        {
            var caseRow = await CaseSql.LoadAsync(scope.UnitOfWork, command.CaseId, ct);
            if (caseRow is null)
            {
                return NotFound("case");
            }

            var requestId = scope.NewId();
            var relationship = await RelationshipAsync(scope, caseRow, ct);
            if (scope.Refuse(BusinessAction.RegisterOutgoingRequest, relationship, AuditEntityTypes.Request, requestId, caseRow.Id) is { } refused)
            {
                return refused;
            }

            if (CaseRules.AcceptsNewWork(caseRow.State) is { IsAllowed: false } closed)
            {
                return Rule(closed);
            }

            var subject = Blank(command.Subject);
            var letterNumber = Blank(command.OutgoingLetterNumber);
            if (subject is null)
            {
                return Invalid("validation.required", nameof(command.Subject));
            }

            if (letterNumber is null)
            {
                return Invalid("validation.required", nameof(command.OutgoingLetterNumber));
            }

            if (command.SentDate > calendar.Today)
            {
                return Invalid("validation.date_in_future", nameof(command.SentDate));
            }

            if (command.DueDate is { } due && due < command.SentDate)
            {
                return Invalid("validation.due_before_sent", nameof(command.DueDate));
            }

            // ADR-041: the deadline is the external sent date + 10 calendar days unless the person changed the
            // suggestion. Recording the basis as INTERNAL only when the suggestion stood keeps the row truthful —
            // a hand-entered date says nothing about whether it is statutory, internal or agreed.
            var dueDate = command.DueDate ?? RequestDeadline.Suggest(command.SentDate);
            var deadlineIsDepartmentDefault = RequestDeadline.IsDefaultFor(command.SentDate, dueDate);

            var target = await scope.UnitOfWork.Command("SELECT is_active, is_own_organization FROM rcs.organization WHERE id = @id")
                .With("id", command.TargetOrganizationId)
                .SingleOrDefaultAsync(reader => new { Active = reader.Bool("is_active"), Own = reader.Bool("is_own_organization") }, ct);
            if (target is not { Active: true, Own: false })
            {
                return Invalid("validation.organization_invalid", nameof(command.TargetOrganizationId));
            }

            if (command.SourceRequirementId is { } requirementId)
            {
                var source = await RequirementSql.LoadAsync(scope.UnitOfWork, requirementId, ct);
                if (source is null || source.CaseId != caseRow.Id)
                {
                    return Invalid("validation.requirement_invalid", nameof(command.SourceRequirementId));
                }

                if (RequestRules.CanSpawnChildRequest(source.Status) is { IsAllowed: false } notOpen)
                {
                    return Rule(notOpen);
                }
            }

            var ownOrganizationId = await CaseSql.OwnOrganizationIdAsync(scope.UnitOfWork, ct);
            if (ownOrganizationId is null)
            {
                return CommandResult<Guid>.Failure(CommandErrorKind.RuleViolation, "organization.own_missing");
            }

            // One outgoing letter may carry several requests (decision C-1): the same registry number, in the same case,
            // to the same organization on the same date, is that same letter.
            var sentAt = calendar.StartOfDay(command.SentDate);
            var existingLetter = await scope.UnitOfWork.Command("""
                    SELECT id, case_id, recipient_organization_id, letter_date
                    FROM rcs.correspondence
                    WHERE direction = 'OUT' AND registry_number = @number
                    """)
                .With("number", letterNumber)
                .SingleOrDefaultAsync(reader => new
                {
                    Id = reader.Uuid("id"),
                    CaseId = reader.Uuid("case_id"),
                    Recipient = reader.Uuid("recipient_organization_id"),
                    LetterDate = reader.DateOrNull("letter_date"),
                }, ct);

            Guid letterId;
            if (existingLetter is not null)
            {
                if (existingLetter.CaseId != caseRow.Id)
                {
                    return Invalid("correspondence.number_used_in_another_case", nameof(command.OutgoingLetterNumber));
                }

                if (existingLetter.Recipient != command.TargetOrganizationId)
                {
                    return Invalid("correspondence.number_used_for_another_organization", nameof(command.OutgoingLetterNumber));
                }

                if (existingLetter.LetterDate != command.SentDate)
                {
                    return Invalid("correspondence.number_used_with_another_date", nameof(command.SentDate));
                }

                letterId = existingLetter.Id;
            }
            else
            {
                letterId = scope.NewId();
                await scope.UnitOfWork.Command("""
                        INSERT INTO rcs.correspondence (id, case_id, direction, correspondence_kind_id, sender_organization_id, recipient_organization_id,
                                                        letter_date, registry_number, registered_at, registered_by_user_id, sent_at, subject, status, created_by_user_id)
                        VALUES (@id, @case, 'OUT', (SELECT id FROM rcs.correspondence_kind WHERE code = 'OUTGOING_REQUEST'), @sender, @recipient,
                                @letter_date, @registry_number, @now, @actor, @sent_at, @subject, 'SENT', @actor)
                        """)
                    .With("id", letterId)
                    .With("case", caseRow.Id)
                    .With("sender", ownOrganizationId)
                    .With("recipient", command.TargetOrganizationId)
                    .With("letter_date", command.SentDate)
                    .With("registry_number", letterNumber)
                    .With("now", scope.Now)
                    .With("actor", scope.Actor.UserId)
                    .With("sent_at", sentAt)
                    .With("subject", subject)
                    .ExecuteAsync(ct);

                await scope.AuditAsync(new AuditEntry(AuditActionCodes.Create, AuditEntityTypes.Correspondence, letterId, 1, caseRow.Id,
                    After: new { direction = "OUT", kind = CorrespondenceKindCodes.OutgoingRequest, registry_number = letterNumber, letter_date = command.SentDate, sent_at = sentAt, status = "SENT" },
                    OccurredAt: sentAt), ct);
            }

            var requestNumber = await NextRequestNumberAsync(scope.UnitOfWork, caseRow, ct);
            var dueAt = calendar.EndOfDay(dueDate);
            var deadlineBasisId = deadlineIsDepartmentDefault
                ? await PostgresLookupQueries.ActiveIdAsync(scope.UnitOfWork, LookupKind.DeadlineBasis, DeadlineBasisCodes.Internal, ct)
                : null;

            await scope.UnitOfWork.Command("""
                    INSERT INTO rcs.request (id, case_id, request_number, target_organization_id, source_requirement_id, dispatch_correspondence_id,
                                             subject, requested_items_note, due_at, deadline_basis_id, status, created_by_user_id)
                    VALUES (@id, @case, @request_number, @target, @source_requirement, @dispatch, @subject, @note, @due_at, @deadline_basis, 'SENT', @actor)
                    """)
                .With("id", requestId)
                .With("case", caseRow.Id)
                .With("request_number", requestNumber)
                .With("target", command.TargetOrganizationId)
                .With("source_requirement", command.SourceRequirementId)
                .With("dispatch", letterId)
                .With("subject", subject)
                .With("note", Blank(command.RequestedItemsNote))
                .With("due_at", dueAt)
                .With("deadline_basis", deadlineBasisId)
                .With("actor", scope.Actor.UserId)
                .ExecuteAsync(ct);

            await scope.AuditAsync(new AuditEntry(AuditActionCodes.Create, AuditEntityTypes.Request, requestId, 1, caseRow.Id,
                After: new
                {
                    request_number = requestNumber,
                    target_organization_id = command.TargetOrganizationId,
                    source_requirement_id = command.SourceRequirementId,
                    dispatch_correspondence_id = letterId,
                    status = "SENT",
                    due_at = dueAt,
                    deadline_basis = deadlineIsDepartmentDefault ? DeadlineBasisCodes.Internal : null,
                },
                OccurredAt: sentAt), ct);

            // Q2: a child request reaching SENT starts its requirement.
            if (command.SourceRequirementId is { } startedRequirement)
            {
                await RequirementSql.StartIfOpenAsync(scope, startedRequirement, caseRow.Id, ActorKind.System, ct);
            }

            await CaseSql.ActivateIfRegisteredAsync(scope, caseRow, ct);
            return CommandResult<Guid>.Success(requestId);
        }, cancellationToken);
    }

    public Task<CommandResult<Guid>> CloseRequestAsync(ActorContext actor, CloseRequestCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        return runner.RunAsync(actor, "request.close", operationId: null, command.CaseId, async (scope, ct) =>
        {
            var caseRow = await CaseSql.LoadAsync(scope.UnitOfWork, command.CaseId, ct);
            if (caseRow is null)
            {
                return NotFound("case");
            }

            var request = await RequestSql.LoadAsync(scope.UnitOfWork, command.RequestId, ct);
            if (request is null || request.CaseId != caseRow.Id)
            {
                return NotFound("request");
            }

            var relationship = await RelationshipAsync(scope, caseRow, ct);
            if (scope.Refuse(BusinessAction.CloseRequest, relationship, AuditEntityTypes.Request, request.Id, caseRow.Id) is { } refused)
            {
                return refused;
            }

            var responses = await RequestSql.ResponseFactsAsync(scope.UnitOfWork, request.Id, ct);
            var requirements = await RequestSql.RequirementFactsAsync(scope.UnitOfWork, request.Id, ct);
            if (RequestRules.CanClose(request.Status, responses, requirements) is { IsAllowed: false } refusedByGuard)
            {
                return Rule(refusedByGuard);
            }

            var note = Blank(command.Note);
            var version = await scope.UnitOfWork.Command("""
                    UPDATE rcs.request
                    SET status = 'CLOSED', closed_at = @now, closed_by_user_id = @actor, closure_note = @note,
                        updated_at = @now, updated_by_user_id = @actor, row_version = row_version + 1
                    WHERE id = @id AND row_version = @row_version
                    RETURNING row_version
                    """)
                .With("now", scope.Now)
                .With("actor", scope.Actor.UserId)
                .With("note", note)
                .With("id", request.Id)
                .With("row_version", command.RowVersion)
                .ScalarAsync<int?>(ct);
            if (version is null)
            {
                return Conflict();
            }

            await scope.AuditAsync(new AuditEntry(AuditActionCodes.StateChange, AuditEntityTypes.Request, request.Id, version, caseRow.Id,
                Before: new { status = request.Status.ToCode() }, After: new { status = "CLOSED", closed_at = scope.Now }, ReasonNote: note), ct);

            return CommandResult<Guid>.Success(request.Id);
        }, cancellationToken);
    }

    // --------------------------------------------------------------- responses

    public Task<CommandResult<Guid>> RegisterResponseAsync(ActorContext actor, RegisterResponseCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        return runner.RunAsync(actor, "response.register", command.OperationId, command.CaseId, async (scope, ct) =>
        {
            var caseRow = await CaseSql.LoadAsync(scope.UnitOfWork, command.CaseId, ct);
            if (caseRow is null)
            {
                return NotFound("case");
            }

            var request = await RequestSql.LoadAsync(scope.UnitOfWork, command.RequestId, ct);
            if (request is null || request.CaseId != caseRow.Id)
            {
                return NotFound("request");
            }

            var responseId = scope.NewId();
            var relationship = await RelationshipAsync(scope, caseRow, ct);
            if (scope.Refuse(BusinessAction.RegisterResponse, relationship, AuditEntityTypes.Response, responseId, caseRow.Id) is { } refused)
            {
                return refused;
            }

            // A response that creates new work is not filed into a closed dossier; it is reopened first (WORKFLOW.md §4.6).
            if (CaseRules.AcceptsNewWork(caseRow.State) is { IsAllowed: false } closed)
            {
                return Rule(closed);
            }

            if (request.Status == RequestStatus.Draft)
            {
                return Rule(RuleCheck.Fail("request.not_issued"));
            }

            var letterNumber = Blank(command.IncomingLetterNumber);
            if (letterNumber is null)
            {
                return Invalid("validation.required", nameof(command.IncomingLetterNumber));
            }

            var today = calendar.Today;
            if (command.LetterDate > today)
            {
                return Invalid("validation.date_in_future", nameof(command.LetterDate));
            }

            var receivedDate = command.ReceivedDate ?? command.LetterDate;
            if (receivedDate > today)
            {
                return Invalid("validation.date_in_future", nameof(command.ReceivedDate));
            }

            if (receivedDate < command.LetterDate)
            {
                return Invalid("validation.received_before_letter_date", nameof(command.ReceivedDate));
            }

            var typeId = await PostgresLookupQueries.ActiveIdAsync(scope.UnitOfWork, LookupKind.ResponseType, command.ResponseTypeCode ?? string.Empty, ct);
            if (typeId is null)
            {
                return Invalid("validation.required", nameof(command.ResponseTypeCode));
            }

            var outcomeId = await PostgresLookupQueries.ActiveIdAsync(scope.UnitOfWork, LookupKind.ResponseOutcome, command.ResponseOutcomeCode ?? string.Empty, ct);
            if (outcomeId is null)
            {
                return Invalid("validation.required", nameof(command.ResponseOutcomeCode));
            }

            var ownOrganizationId = await CaseSql.OwnOrganizationIdAsync(scope.UnitOfWork, ct);
            if (ownOrganizationId is null)
            {
                return CommandResult<Guid>.Failure(CommandErrorKind.RuleViolation, "organization.own_missing");
            }

            // One incoming letter may carry several responses (N:1): the same letter number and date from the same
            // organization in this case is that same letter.
            var receivedAt = calendar.StartOfDay(receivedDate);
            var registryNumber = Blank(command.IncomingRegistryNumber);
            var letterId = await scope.UnitOfWork.Command("""
                    SELECT id FROM rcs.correspondence
                    WHERE case_id = @case AND direction = 'IN' AND sender_organization_id = @sender
                      AND letter_number = @number AND letter_date IS NOT DISTINCT FROM @letter_date AND status <> 'VOID'
                    """)
                .With("case", caseRow.Id)
                .With("sender", request.TargetOrganizationId)
                .With("number", letterNumber)
                .With("letter_date", command.LetterDate)
                .ScalarAsync<Guid?>(ct);

            if (letterId is null)
            {
                var newLetterId = scope.NewId();
                await scope.UnitOfWork.Command("""
                        INSERT INTO rcs.correspondence (id, case_id, direction, correspondence_kind_id, sender_organization_id, recipient_organization_id,
                                                        letter_number, letter_date, registry_number, registered_at, registered_by_user_id, received_at,
                                                        parent_correspondence_id, subject, status, created_by_user_id)
                        VALUES (@id, @case, 'IN', (SELECT id FROM rcs.correspondence_kind WHERE code = 'INCOMING_RESPONSE'), @sender, @recipient,
                                @letter_number, @letter_date, @registry_number, @now, @actor, @received_at,
                                @parent, @subject, 'RECEIVED', @actor)
                        """)
                    .With("id", newLetterId)
                    .With("case", caseRow.Id)
                    .With("sender", request.TargetOrganizationId)
                    .With("recipient", ownOrganizationId)
                    .With("letter_number", letterNumber)
                    .With("letter_date", command.LetterDate)
                    .With("registry_number", registryNumber)
                    .With("now", scope.Now)
                    .With("actor", scope.Actor.UserId)
                    .With("received_at", receivedAt)
                    .With("parent", request.DispatchCorrespondenceId)
                    .With("subject", Blank(command.Summary))
                    .ExecuteAsync(ct);

                await scope.AuditAsync(new AuditEntry(AuditActionCodes.Create, AuditEntityTypes.Correspondence, newLetterId, 1, caseRow.Id,
                    After: new { direction = "IN", kind = CorrespondenceKindCodes.IncomingResponse, letter_number = letterNumber, letter_date = command.LetterDate, received_at = receivedAt, status = "RECEIVED" },
                    OccurredAt: receivedAt), ct);
                letterId = newLetterId;
            }
            else if (await scope.UnitOfWork.Command("SELECT EXISTS (SELECT 1 FROM rcs.response WHERE request_id = @request AND correspondence_id = @letter AND status <> 'VOID')")
                         .With("request", request.Id).With("letter", letterId.Value).ScalarAsync<bool>(ct))
            {
                return Rule(RuleCheck.Fail("response.already_registered_for_letter"));
            }

            await scope.UnitOfWork.Command("""
                    INSERT INTO rcs.response (id, request_id, correspondence_id, response_type_id, response_outcome_id, is_conclusive,
                                              summary, response_date, received_at, recorded_at, recorded_by_user_id, status, created_by_user_id)
                    VALUES (@id, @request, @letter, @type, @outcome, @is_conclusive,
                            @summary, @response_date, @received_at, @now, @actor, 'ACTIVE', @actor)
                    """)
                .With("id", responseId)
                .With("request", request.Id)
                .With("letter", letterId)
                .With("type", typeId)
                .With("outcome", outcomeId)
                .With("is_conclusive", command.IsConclusive)
                .With("summary", Blank(command.Summary))
                .With("response_date", command.LetterDate)
                .With("received_at", receivedAt)
                .With("now", scope.Now)
                .With("actor", scope.Actor.UserId)
                .ExecuteAsync(ct);

            await scope.AuditAsync(new AuditEntry(AuditActionCodes.Create, AuditEntityTypes.Response, responseId, 1, caseRow.Id,
                After: new
                {
                    request_id = request.Id,
                    correspondence_id = letterId,
                    response_type = command.ResponseTypeCode,
                    response_outcome = command.ResponseOutcomeCode,
                    is_conclusive = command.IsConclusive,
                    received_at = receivedAt,
                },
                OccurredAt: receivedAt), ct);

            await CaseSql.ApplyRequestConsequenceAsync(scope, request.Id, caseRow.Id, ct);
            return CommandResult<Guid>.Success(responseId);
        }, cancellationToken);
    }

    // ------------------------------------------------------------ requirements

    public Task<CommandResult<Guid>> CreateRequirementAsync(ActorContext actor, CreateRequirementCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        return runner.RunAsync(actor, "requirement.create", command.OperationId, command.CaseId, async (scope, ct) =>
        {
            var caseRow = await CaseSql.LoadAsync(scope.UnitOfWork, command.CaseId, ct);
            if (caseRow is null)
            {
                return NotFound("case");
            }

            var source = await scope.UnitOfWork.Command("""
                    SELECT p.id, p.status, p.received_at, p.recorded_by_user_id, r.id AS request_id, r.case_id, r.status AS request_status, r.target_organization_id
                    FROM rcs.response AS p JOIN rcs.request AS r ON r.id = p.request_id
                    WHERE p.id = @id
                    """)
                .With("id", command.SourceResponseId)
                .SingleOrDefaultAsync(reader => new
                {
                    Status = VocabularyCodes.FromCode<ResponseStatus>(reader.Text("status")),
                    ReceivedAt = reader.InstantOrNull("received_at"),
                    RecordedBy = reader.Uuid("recorded_by_user_id"),
                    RequestId = reader.Uuid("request_id"),
                    CaseId = reader.Uuid("case_id"),
                    TargetOrganizationId = reader.Uuid("target_organization_id"),
                }, ct);
            if (source is null || source.CaseId != caseRow.Id)
            {
                return NotFound("response");
            }

            var requirementId = scope.NewId();
            var relationship = (await RelationshipAsync(scope, caseRow, ct)) with { ActorRegisteredSourceResponse = source.RecordedBy == scope.Actor.UserId };
            if (scope.Refuse(BusinessAction.CreateRequirementFromResponse, relationship, AuditEntityTypes.Requirement, requirementId, caseRow.Id) is { } refused)
            {
                return refused;
            }

            if (CaseRules.AcceptsNewWork(caseRow.State) is { IsAllowed: false } closed)
            {
                return Rule(closed);
            }

            if (source.Status != ResponseStatus.Active)
            {
                return Rule(RuleCheck.Fail("response.not_active"));
            }

            var title = Blank(command.Title);
            if (title is null)
            {
                return Invalid("validation.required", nameof(command.Title));
            }

            if (command.AddressedToOrganizationId is { } addressedTo)
            {
                var exists = await scope.UnitOfWork.Command("SELECT EXISTS (SELECT 1 FROM rcs.organization WHERE id = @id AND is_active)")
                    .With("id", addressedTo).ScalarAsync<bool>(ct);
                if (!exists)
                {
                    return Invalid("validation.organization_invalid", nameof(command.AddressedToOrganizationId));
                }
            }

            var raisedAt = source.ReceivedAt ?? scope.Now;
            var dueAt = command.DueDate is { } dueDate ? calendar.EndOfDay(dueDate) : (DateTimeOffset?)null;
            await scope.UnitOfWork.Command("""
                    INSERT INTO rcs.requirement (id, case_id, requirement_origin_type_id, source_response_id, raised_by_organization_id, addressed_to_organization_id,
                                                 title, description, is_blocking, raised_at, due_at, status, created_by_user_id)
                    VALUES (@id, @case, (SELECT id FROM rcs.requirement_origin_type WHERE code = 'RESPONSE'), @source_response, @raised_by, @addressed_to,
                            @title, @description, @is_blocking, @raised_at, @due_at, 'OPEN', @actor)
                    """)
                .With("id", requirementId)
                .With("case", caseRow.Id)
                .With("source_response", command.SourceResponseId)
                .With("raised_by", source.TargetOrganizationId)
                .With("addressed_to", command.AddressedToOrganizationId)
                .With("title", title)
                .With("description", Blank(command.Description))
                .With("is_blocking", command.IsBlocking)
                .With("raised_at", raisedAt)
                .With("due_at", dueAt)
                .With("actor", scope.Actor.UserId)
                .ExecuteAsync(ct);

            await scope.AuditAsync(new AuditEntry(AuditActionCodes.Create, AuditEntityTypes.Requirement, requirementId, 1, caseRow.Id,
                After: new
                {
                    title,
                    origin_type = RequirementOriginCodes.Response,
                    source_response_id = command.SourceResponseId,
                    is_blocking = command.IsBlocking,
                    addressed_to_organization_id = command.AddressedToOrganizationId,
                    due_at = dueAt,
                    status = "OPEN",
                },
                OccurredAt: raisedAt), ct);

            // R9: new blocking work under a closed request returns it to ANSWERED.
            await CaseSql.ApplyRequestConsequenceAsync(scope, source.RequestId, caseRow.Id, ct);
            await CaseSql.ActivateIfRegisteredAsync(scope, caseRow, ct);
            return CommandResult<Guid>.Success(requirementId);
        }, cancellationToken);
    }

    public Task<CommandResult<Guid>> StartRequirementAsync(ActorContext actor, StartRequirementCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        return ResolveAsync(actor, "requirement.start", command.CaseId, command.RequirementId, command.RowVersion, BusinessAction.StartRequirement,
            (_, requirement) => RequirementRules.CanStart(requirement.Status),
            async (scope, caseRow, requirement, ct) =>
            {
                var version = await RequirementSql.UpdateAsync(scope, requirement, command.RowVersion, """
                        status = 'IN_PROGRESS', started_at = COALESCE(started_at, @now)
                        """, ct);
                if (version is null)
                {
                    return Conflict();
                }

                await scope.AuditAsync(new AuditEntry(AuditActionCodes.StateChange, AuditEntityTypes.Requirement, requirement.Id, version, caseRow.Id,
                    Before: new { status = requirement.Status.ToCode() }, After: new { status = "IN_PROGRESS" }), ct);
                return CommandResult<Guid>.Success(requirement.Id);
            }, cancellationToken);
    }

    public Task<CommandResult<Guid>> FulfillRequirementAsync(ActorContext actor, FulfillRequirementCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        return runner.RunAsync(actor, "requirement.fulfil", operationId: null, command.CaseId, async (scope, ct) =>
        {
            var loaded = await LoadRequirementAsync(scope, command.CaseId, command.RequirementId, BusinessAction.FulfillRequirement, null, ct);
            if (loaded.Failure is { } failure)
            {
                return failure;
            }

            var (caseRow, requirement) = (loaded.Case!, loaded.Requirement!);

            if (command.EvidenceResponseId is { } evidenceResponseId)
            {
                var valid = await scope.UnitOfWork.Command("""
                        SELECT EXISTS (
                            SELECT 1 FROM rcs.response AS p JOIN rcs.request AS r ON r.id = p.request_id
                            WHERE p.id = @id AND p.status = 'ACTIVE' AND r.case_id = @case)
                        """)
                    .With("id", evidenceResponseId).With("case", caseRow.Id).ScalarAsync<bool>(ct);
                if (!valid)
                {
                    return Invalid("validation.evidence_invalid", nameof(command.EvidenceResponseId));
                }
            }

            var activeEvidence = await scope.UnitOfWork.Command("SELECT count(*) FROM rcs.requirement_evidence WHERE requirement_id = @id AND status = 'ACTIVE'")
                .With("id", requirement.Id).ScalarAsync<long>(ct);
            var note = Blank(command.ResolutionNote);
            var evidenceAfterwards = (int)activeEvidence + (command.EvidenceResponseId is null ? 0 : 1);
            if (RequirementRules.CanFulfill(requirement.Status, evidenceAfterwards, note) is { IsAllowed: false } refusedByGuard)
            {
                return Rule(refusedByGuard);
            }

            if (command.EvidenceResponseId is { } responseId)
            {
                var evidenceId = scope.NewId();
                await scope.UnitOfWork.Command("""
                        INSERT INTO rcs.requirement_evidence (id, requirement_id, evidence_type, response_id, note, is_primary, recorded_by_user_id, recorded_at, status, created_by_user_id)
                        VALUES (@id, @requirement, 'RESPONSE', @response, @note, true, @actor, @now, 'ACTIVE', @actor)
                        """)
                    .With("id", evidenceId)
                    .With("requirement", requirement.Id)
                    .With("response", responseId)
                    .With("note", note)
                    .With("actor", scope.Actor.UserId)
                    .With("now", scope.Now)
                    .ExecuteAsync(ct);

                await scope.AuditAsync(new AuditEntry(AuditActionCodes.Link, AuditEntityTypes.RequirementEvidence, evidenceId, 1, caseRow.Id,
                    After: new { requirement_id = requirement.Id, evidence_type = "RESPONSE", response_id = responseId }), ct);
            }

            var version = await RequirementSql.UpdateAsync(scope, requirement, command.RowVersion, """
                    status = 'FULFILLED', resolved_at = @now, resolved_by_user_id = @actor, resolution_note = @note
                    """, ct, cmd => cmd.With("note", note));
            if (version is null)
            {
                return Conflict();
            }

            await scope.AuditAsync(new AuditEntry(AuditActionCodes.StateChange, AuditEntityTypes.Requirement, requirement.Id, version, caseRow.Id,
                Before: new { status = requirement.Status.ToCode() },
                After: new { status = "FULFILLED", resolved_at = scope.Now, evidence_response_id = command.EvidenceResponseId },
                ReasonNote: note), ct);

            await RequirementSql.ApplyParentRequestConsequenceAsync(scope, requirement, caseRow.Id, ct);
            return CommandResult<Guid>.Success(requirement.Id);
        }, cancellationToken);
    }

    public Task<CommandResult<Guid>> WaiveRequirementAsync(ActorContext actor, WaiveRequirementCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        return runner.RunAsync(actor, "requirement.waive", operationId: null, command.CaseId, async (scope, ct) =>
        {
            var loaded = await LoadRequirementAsync(scope, command.CaseId, command.RequirementId, BusinessAction.WaiveRequirement, null, ct);
            if (loaded.Failure is { } failure)
            {
                return failure;
            }

            var (caseRow, requirement) = (loaded.Case!, loaded.Requirement!);
            var note = Blank(command.Note);
            if (RequirementRules.CanWaive(requirement.Status, command.WaiverReasonCode, note) is { IsAllowed: false } refusedByGuard)
            {
                return Rule(refusedByGuard);
            }

            var reasonId = await PostgresLookupQueries.ActiveIdAsync(scope.UnitOfWork, LookupKind.WaiverReason, command.WaiverReasonCode, ct);
            if (reasonId is null)
            {
                return Invalid("validation.required", nameof(command.WaiverReasonCode));
            }

            var version = await RequirementSql.UpdateAsync(scope, requirement, command.RowVersion, """
                    status = 'WAIVED', resolved_at = @now, resolved_by_user_id = @actor, resolution_note = @note,
                    waiver_authorised_by_user_id = @actor, waiver_reason_id = @reason
                    """, ct, cmd => cmd.With("note", note).With("reason", reasonId));
            if (version is null)
            {
                return Conflict();
            }

            await scope.AuditAsync(new AuditEntry(AuditActionCodes.StateChange, AuditEntityTypes.Requirement, requirement.Id, version, caseRow.Id,
                Before: new { status = requirement.Status.ToCode() },
                After: new { status = "WAIVED", waiver_reason = command.WaiverReasonCode, waiver_authorised_by_user_id = scope.Actor.UserId, resolved_at = scope.Now },
                ReasonNote: note), ct);

            await RequirementSql.ApplyParentRequestConsequenceAsync(scope, requirement, caseRow.Id, ct);
            return CommandResult<Guid>.Success(requirement.Id);
        }, cancellationToken);
    }

    public Task<CommandResult<Guid>> VoidRequirementAsync(ActorContext actor, VoidRequirementCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        return runner.RunAsync(actor, "requirement.void", operationId: null, command.CaseId, async (scope, ct) =>
        {
            var loaded = await LoadRequirementAsync(scope, command.CaseId, command.RequirementId, BusinessAction.VoidRequirement, command.VoidReasonCode, ct);
            if (loaded.Failure is { } failure)
            {
                return failure;
            }

            var (caseRow, requirement) = (loaded.Case!, loaded.Requirement!);
            var note = Blank(command.Note);
            if (RequirementRules.CanVoid(requirement.Status, command.VoidReasonCode, note) is { IsAllowed: false } refusedByGuard)
            {
                return Rule(refusedByGuard);
            }

            var reasonId = await PostgresLookupQueries.ActiveIdAsync(scope.UnitOfWork, LookupKind.VoidReason, command.VoidReasonCode, ct);
            if (reasonId is null)
            {
                return Invalid("validation.required", nameof(command.VoidReasonCode));
            }

            if (command.VoidSourceResponseId is { } sourceResponseId)
            {
                var valid = await scope.UnitOfWork.Command("""
                        SELECT EXISTS (SELECT 1 FROM rcs.response AS p JOIN rcs.request AS r ON r.id = p.request_id WHERE p.id = @id AND r.case_id = @case)
                        """)
                    .With("id", sourceResponseId).With("case", caseRow.Id).ScalarAsync<bool>(ct);
                if (!valid)
                {
                    return Invalid("validation.evidence_invalid", nameof(command.VoidSourceResponseId));
                }
            }

            var version = await RequirementSql.UpdateAsync(scope, requirement, command.RowVersion, """
                    status = 'VOID', resolved_at = @now, resolved_by_user_id = @actor, resolution_note = @note,
                    void_reason_id = @reason, voided_by_user_id = @actor, void_source_response_id = @void_source
                    """, ct, cmd => cmd.With("note", note).With("reason", reasonId).With("void_source", command.VoidSourceResponseId));
            if (version is null)
            {
                return Conflict();
            }

            await scope.AuditAsync(new AuditEntry(AuditActionCodes.StateChange, AuditEntityTypes.Requirement, requirement.Id, version, caseRow.Id,
                Before: new { status = requirement.Status.ToCode() },
                After: new { status = "VOID", void_reason = command.VoidReasonCode, void_source_response_id = command.VoidSourceResponseId, resolved_at = scope.Now },
                ReasonNote: note), ct);

            await RequirementSql.ApplyParentRequestConsequenceAsync(scope, requirement, caseRow.Id, ct);
            return CommandResult<Guid>.Success(requirement.Id);
        }, cancellationToken);
    }

    public Task<CommandResult<Guid>> FailRequirementAsync(ActorContext actor, FailRequirementCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        return runner.RunAsync(actor, "requirement.fail", operationId: null, command.CaseId, async (scope, ct) =>
        {
            var loaded = await LoadRequirementAsync(scope, command.CaseId, command.RequirementId, BusinessAction.FailRequirement, null, ct);
            if (loaded.Failure is { } failure)
            {
                return failure;
            }

            var (caseRow, requirement) = (loaded.Case!, loaded.Requirement!);
            var note = Blank(command.FailureReasonNote);
            if (RequirementRules.CanFail(requirement.Status, note) is { IsAllowed: false } refusedByGuard)
            {
                return Rule(refusedByGuard);
            }

            var version = await RequirementSql.UpdateAsync(scope, requirement, command.RowVersion, """
                    status = 'FAILED', resolved_at = @now, resolved_by_user_id = @actor, failure_reason_note = @note
                    """, ct, cmd => cmd.With("note", note));
            if (version is null)
            {
                return Conflict();
            }

            await scope.AuditAsync(new AuditEntry(AuditActionCodes.StateChange, AuditEntityTypes.Requirement, requirement.Id, version, caseRow.Id,
                Before: new { status = requirement.Status.ToCode() },
                After: new { status = "FAILED", resolved_at = scope.Now },
                ReasonNote: note), ct);

            await RequirementSql.ApplyParentRequestConsequenceAsync(scope, requirement, caseRow.Id, ct);
            return CommandResult<Guid>.Success(requirement.Id);
        }, cancellationToken);
    }

    // ---------------------------------------------------------------- plumbing

    private Task<CommandResult<Guid>> ResolveAsync(
        ActorContext actor,
        string operationKind,
        Guid caseId,
        Guid requirementId,
        int rowVersion,
        BusinessAction action,
        Func<CaseRow, RequirementRow, RuleCheck> guard,
        Func<CommandScope, CaseRow, RequirementRow, CancellationToken, Task<CommandResult<Guid>>> apply,
        CancellationToken cancellationToken) =>
        runner.RunAsync(actor, operationKind, operationId: null, caseId, async (scope, ct) =>
        {
            var loaded = await LoadRequirementAsync(scope, caseId, requirementId, action, null, ct);
            if (loaded.Failure is { } failure)
            {
                return failure;
            }

            var (caseRow, requirement) = (loaded.Case!, loaded.Requirement!);
            if (guard(caseRow, requirement) is { IsAllowed: false } refused)
            {
                return Rule(refused);
            }

            return await apply(scope, caseRow, requirement, ct);
        }, cancellationToken);

    private static async Task<(CaseRow? Case, RequirementRow? Requirement, CommandResult<Guid>? Failure)> LoadRequirementAsync(
        CommandScope scope, Guid caseId, Guid requirementId, BusinessAction action, string? voidReasonCode, CancellationToken cancellationToken)
    {
        var caseRow = await CaseSql.LoadAsync(scope.UnitOfWork, caseId, cancellationToken);
        if (caseRow is null)
        {
            return (null, null, NotFound("case"));
        }

        var requirement = await RequirementSql.LoadAsync(scope.UnitOfWork, requirementId, cancellationToken);
        if (requirement is null || requirement.CaseId != caseRow.Id)
        {
            return (null, null, NotFound("requirement"));
        }

        var hasDependents = await RequirementSql.HasDependentsAsync(scope.UnitOfWork, requirement.Id, cancellationToken);
        var relationship = (await RelationshipAsync(scope, caseRow, cancellationToken)) with
        {
            ActorCreatedTargetWithoutDependents = requirement.CreatedByUserId == scope.Actor.UserId && !hasDependents,
            VoidReasonCode = voidReasonCode,
        };

        if (scope.Refuse(action, relationship, AuditEntityTypes.Requirement, requirement.Id, caseRow.Id) is { } refused)
        {
            return (null, null, refused);
        }

        if (CaseRules.AcceptsNewWork(caseRow.State) is { IsAllowed: false } closed)
        {
            return (null, null, Rule(closed));
        }

        return (caseRow, requirement, null);
    }

    private static async Task<CaseRelationship> RelationshipAsync(CommandScope scope, CaseRow caseRow, CancellationToken cancellationToken) =>
        new(caseRow.IsRestricted, await CaseSql.ActorIsAssignedAsync(scope.UnitOfWork, caseRow.Id, scope.Actor.UserId, scope.Now, cancellationToken));

    private static async Task<string> NextRequestNumberAsync(PostgresUnitOfWork unitOfWork, CaseRow caseRow, CancellationToken cancellationToken)
    {
        // Inside the case lock, so the count cannot race. PROVISIONAL format (OQ-7): "<case number>-S<n>".
        var count = await unitOfWork.Command("SELECT count(*) FROM rcs.request WHERE case_id = @case").With("case", caseRow.Id).ScalarAsync<long>(cancellationToken);
        return $"{caseRow.CaseNumber}-S{count + 1}";
    }

    private static CommandResult<Guid> NotFound(string what) => CommandResult<Guid>.Failure(CommandErrorKind.NotFound, $"notfound.{what}");

    private static CommandResult<Guid> Rule(RuleCheck check) => CommandResult<Guid>.Failure(CommandErrorKind.RuleViolation, check.ViolationCode!);

    private static CommandResult<Guid> Invalid(string code, string field) => CommandResult<Guid>.Failure(CommandErrorKind.Validation, code, field);

    private static CommandResult<Guid> Conflict() => CommandResult<Guid>.Failure(CommandErrorKind.Conflict, "concurrency.changed_elsewhere");
}
