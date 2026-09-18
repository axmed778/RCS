using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using Rcs.Application.Cases;
using Rcs.Application.Common;
using Rcs.Domain.Authorization;
using Rcs.Domain.Vocabulary;
using Rcs.Infrastructure.Audit;
using Rcs.Infrastructure.Commands;
using Rcs.Application.Identity;
using Rcs.Infrastructure.Identity;
using Rcs.Infrastructure.Persistence;
using static Rcs.Infrastructure.Organizations.PostgresOrganizationService;

namespace Rcs.Infrastructure.Cases;

/// <summary>
/// Case registration (WORKFLOW.md §2.1): the already-received initiating letter, the case it opens, its first
/// lifecycle row and — when a responsible employee is chosen — the RESPONSIBLE assignment, in one transaction.
/// </summary>
internal sealed class PostgresCaseService(CommandRunner runner, BusinessCalendar calendar) : ICaseService
{
    /// <summary>Serialises case-number allocation; namespaced away from case and migration locks.</summary>
    private static readonly long CaseNumberLockKey = BinaryPrimitives.ReadInt64BigEndian(SHA256.HashData("rcs:case-numbering:v1"u8));

    public Task<CommandResult<Guid>> CreateAsync(ActorContext actor, CreateCaseCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        return runner.RunAsync(actor, "case.create", command.OperationId, caseId: null, async (scope, ct) =>
        {
            var caseId = scope.NewId();
            if (scope.Refuse(BusinessAction.CreateCase, null, AuditEntityTypes.Case, caseId, null) is { } refused)
            {
                return refused;
            }

            if (command.ResponsibleUserId is not null
                && scope.Refuse(BusinessAction.AssignCase, null, AuditEntityTypes.Case, caseId, null) is { } cannotAssign)
            {
                return cannotAssign;
            }

            var title = Blank(command.Title);
            var letterNumber = Blank(command.IncomingLetterNumber);
            if (title is null)
            {
                return Invalid("validation.required", nameof(command.Title));
            }

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

            var requester = await scope.UnitOfWork.Command("SELECT is_active, is_own_organization FROM rcs.organization WHERE id = @id")
                .With("id", command.RequestingOrganizationId)
                .SingleOrDefaultAsync(reader => new { Active = reader.Bool("is_active"), Own = reader.Bool("is_own_organization") }, ct);
            if (requester is not { Active: true, Own: false })
            {
                return Invalid("validation.organization_invalid", nameof(command.RequestingOrganizationId));
            }

            var ownOrganizationId = await CaseSql.OwnOrganizationIdAsync(scope.UnitOfWork, ct);
            if (ownOrganizationId is null)
            {
                return CommandResult<Guid>.Failure(CommandErrorKind.RuleViolation, "organization.own_missing");
            }

            if (command.ResponsibleUserId is { } responsibleId)
            {
                var responsible = await ActorStore.LoadAsync(scope.UnitOfWork, responsibleId, scope.Now, ct);
                if (responsible is null || responsible.Status != UserStatus.Active || !responsible.Authority().HasBusinessRole)
                {
                    return Invalid("validation.user_invalid", nameof(command.ResponsibleUserId));
                }
            }

            var registeredAt = calendar.StartOfDay(receivedDate);
            var caseNumber = await AllocateCaseNumberAsync(scope.UnitOfWork, calendar.ToDate(registeredAt).Year, ct);
            var subject = Blank(command.Subject);
            var notes = Blank(command.Notes);

            await scope.UnitOfWork.Command("""
                    INSERT INTO rcs.case_record (id, case_number, title, subject, requesting_organization_id, lifecycle_state, registered_at, notes, created_at, created_by_user_id)
                    VALUES (@id, @case_number, @title, @subject, @requester, 'REGISTERED', @registered_at, @notes, @now, @actor)
                    """)
                .With("id", caseId)
                .With("case_number", caseNumber)
                .With("title", title)
                .With("subject", subject)
                .With("requester", command.RequestingOrganizationId)
                .With("registered_at", registeredAt)
                .With("notes", notes)
                .With("now", scope.Now)
                .With("actor", scope.Actor.UserId)
                .ExecuteAsync(ct);

            await scope.UnitOfWork.Command("""
                    INSERT INTO rcs.case_state_change (id, case_id, from_state, to_state, occurred_at, actor_user_id)
                    VALUES (@id, @case, NULL, 'REGISTERED', @occurred_at, @actor)
                    """)
                .With("id", scope.NewId())
                .With("case", caseId)
                .With("occurred_at", registeredAt)
                .With("actor", scope.Actor.UserId)
                .ExecuteAsync(ct);

            await scope.AuditAsync(new AuditEntry(AuditActionCodes.Create, AuditEntityTypes.Case, caseId, 1, caseId,
                After: new { case_number = caseNumber, title, requesting_organization_id = command.RequestingOrganizationId, lifecycle_state = "REGISTERED", registered_at = registeredAt },
                OccurredAt: registeredAt), ct);

            // The initiating letter: received in the external system, recorded here (ADR-006).
            var letterId = scope.NewId();
            var registryNumber = Blank(command.IncomingRegistryNumber);
            await scope.UnitOfWork.Command("""
                    INSERT INTO rcs.correspondence (id, case_id, direction, correspondence_kind_id, sender_organization_id, recipient_organization_id,
                                                    letter_number, letter_date, registry_number, registered_at, registered_by_user_id, received_at,
                                                    subject, status, created_by_user_id)
                    VALUES (@id, @case, 'IN', (SELECT id FROM rcs.correspondence_kind WHERE code = 'INITIATING'), @sender, @recipient,
                            @letter_number, @letter_date, @registry_number, @now, @actor, @received_at,
                            @subject, 'RECEIVED', @actor)
                    """)
                .With("id", letterId)
                .With("case", caseId)
                .With("sender", command.RequestingOrganizationId)
                .With("recipient", ownOrganizationId)
                .With("letter_number", letterNumber)
                .With("letter_date", command.LetterDate)
                .With("registry_number", registryNumber)
                .With("now", scope.Now)
                .With("actor", scope.Actor.UserId)
                .With("received_at", registeredAt)
                .With("subject", title)
                .ExecuteAsync(ct);

            await scope.AuditAsync(new AuditEntry(AuditActionCodes.Create, AuditEntityTypes.Correspondence, letterId, 1, caseId,
                After: new { direction = "IN", kind = CorrespondenceKindCodes.Initiating, letter_number = letterNumber, letter_date = command.LetterDate, registry_number = registryNumber, received_at = registeredAt, status = "RECEIVED" },
                OccurredAt: registeredAt), ct);

            if (command.ResponsibleUserId is { } assigneeId)
            {
                var assignmentId = scope.NewId();
                await scope.UnitOfWork.Command("""
                        INSERT INTO rcs.assignment (id, scope, case_id, assignee_user_id, assignment_role_id, assigned_by_user_id, valid_from, status, created_by_user_id)
                        VALUES (@id, 'CASE', @case, @assignee, (SELECT id FROM rcs.assignment_role WHERE code = 'RESPONSIBLE'), @actor, @valid_from, 'ACTIVE', @actor)
                        """)
                    .With("id", assignmentId)
                    .With("case", caseId)
                    .With("assignee", assigneeId)
                    .With("actor", scope.Actor.UserId)
                    .With("valid_from", registeredAt)
                    .ExecuteAsync(ct);

                await scope.AuditAsync(new AuditEntry(AuditActionCodes.Assign, AuditEntityTypes.Assignment, assignmentId, 1, caseId,
                    After: new { scope = "CASE", assignee_user_id = assigneeId, role = AssignmentRoleCodes.Responsible, valid_from = registeredAt, status = "ACTIVE" }), ct);
            }

            return CommandResult<Guid>.Success(caseId);
        }, cancellationToken);
    }

    /// <summary>
    /// PROVISIONAL numbering (OQ-7: format, reset and scope are undecided): <c>YYYY/NNNN</c>, restarting each year.
    /// Business numbers are never keys, so changing the format later touches no relationship (ADR-031).
    /// </summary>
    private static async Task<string> AllocateCaseNumberAsync(PostgresUnitOfWork unitOfWork, int year, CancellationToken cancellationToken)
    {
        await unitOfWork.Command("SELECT pg_advisory_xact_lock(@key)").WithLong("key", CaseNumberLockKey).ExecuteAsync(cancellationToken);

        var prefix = year.ToString("D4", CultureInfo.InvariantCulture) + "/";
        var numbers = await unitOfWork.Command("SELECT case_number FROM rcs.case_record WHERE left(case_number, 5) = @prefix")
            .With("prefix", prefix)
            .ListAsync(reader => reader.Text("case_number"), cancellationToken);

        var highest = numbers
            .Select(number => int.TryParse(number.AsSpan(5), NumberStyles.None, CultureInfo.InvariantCulture, out var sequence) ? sequence : 0)
            .DefaultIfEmpty(0)
            .Max();

        return prefix + (highest + 1).ToString("D4", CultureInfo.InvariantCulture);
    }

    private static CommandResult<Guid> Invalid(string code, string field) => CommandResult<Guid>.Failure(CommandErrorKind.Validation, code, field);
}
