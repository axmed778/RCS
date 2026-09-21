using Rcs.Application.Common;
using Rcs.Application.Identifiers;
using Rcs.Application.Identity;
using Rcs.Application.Persistence;
using Rcs.Domain.Vocabulary;
using Rcs.Infrastructure.Audit;
using Rcs.Infrastructure.Identity;
using Rcs.Infrastructure.Persistence;

namespace Rcs.Infrastructure.Organizations;

/// <summary>What the installation step did, for the console to print.</summary>
public sealed record DepartmentOutcome(Guid OrganizationId, string OfficialName, string Summary);

/// <summary>
/// Records which organization the department itself is (<c>organization.is_own_organization</c>). Exactly one row
/// carries that flag, and the workflow depends on it: a case is registered by an external applicant and a request is
/// sent to an external authority, both of which are defined by "not us" (WORKFLOW.md §2, §3).
/// </summary>
/// <remarks>
/// A fresh database has no such row — the demo seeder created one for development, and it is Development-only. So a
/// pilot or production installation names the department once, from the console, as part of setting the system up.
/// It is written like any other master data, attributed to the technical administrator who ran it.
/// </remarks>
public sealed class DepartmentBootstrap(
    IUnitOfWorkFactory unitOfWorkFactory,
    AuditWriter audit,
    IIdGenerator ids,
    TimeProvider timeProvider)
{
    public async Task<CommandResult<DepartmentOutcome>> SetOwnOrganizationAsync(
        string officialName, string? shortName, string typeCode, string byUsername, CancellationToken cancellationToken = default)
    {
        await using var unitOfWork = (PostgresUnitOfWork)await unitOfWorkFactory.BeginAsync(cancellationToken);
        var now = timeProvider.GetUtcNow();

        var administrator = await LoadTechAdminAsync(unitOfWork, byUsername, now, cancellationToken);
        if (administrator is null)
        {
            return CommandResult<DepartmentOutcome>.Failure(CommandErrorKind.Forbidden, "auth.bootstrap_actor_not_tech_admin");
        }

        if (string.IsNullOrWhiteSpace(officialName))
        {
            return CommandResult<DepartmentOutcome>.Failure(CommandErrorKind.Validation, "validation.required", "name");
        }

        var typeId = await unitOfWork.Command("SELECT id FROM rcs.organization_type WHERE code = @code AND is_active")
            .With("code", typeCode)
            .ScalarAsync<Guid?>(cancellationToken);
        if (typeId is null)
        {
            return CommandResult<DepartmentOutcome>.Failure(CommandErrorKind.Validation, "organization.type_unknown", "type");
        }

        // One row, ever: if the department is already recorded, say so rather than creating a second one.
        var existing = await unitOfWork.Command("SELECT id, official_name FROM rcs.organization WHERE is_own_organization")
            .ListAsync(reader => (Id: reader.Uuid("id"), Name: reader.Text("official_name")), cancellationToken);
        if (existing.Count > 0)
        {
            return CommandResult<DepartmentOutcome>.Success(new DepartmentOutcome(existing[0].Id, existing[0].Name, "the department is already recorded"));
        }

        var organizationId = ids.NewId();
        await unitOfWork.Command("""
                INSERT INTO rcs.organization (id, official_name, short_name, organization_type_id, is_own_organization, created_at, created_by_user_id)
                VALUES (@id, @official_name, @short_name, @type, true, @now, @by)
                """)
            .With("id", organizationId)
            .With("official_name", officialName.Trim())
            .With("short_name", string.IsNullOrWhiteSpace(shortName) ? officialName.Trim() : shortName.Trim())
            .With("type", typeId.Value)
            .With("now", now)
            .With("by", administrator.UserId)
            .ExecuteAsync(cancellationToken);

        await audit.WriteAsync(unitOfWork, administrator, null, ids.NewId(), new AuditEntry(
            AuditActionCodes.Create, AuditEntityTypes.Organization, organizationId, 1, null,
            After: new { official_name = officialName.Trim(), is_own_organization = true, type = typeCode },
            ReasonNote: "Installation: the department's own organization"), cancellationToken);
        await unitOfWork.CommitAsync(cancellationToken);
        return CommandResult<DepartmentOutcome>.Success(new DepartmentOutcome(organizationId, officialName.Trim(), "the department was recorded"));
    }

    private static async Task<ActorProfile?> LoadTechAdminAsync(PostgresUnitOfWork unitOfWork, string username, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var userId = await unitOfWork.Command("SELECT id FROM rcs.app_user WHERE username = @username")
            .With("username", (username ?? string.Empty).Trim().ToLowerInvariant())
            .ScalarAsync<Guid?>(cancellationToken);
        var profile = userId is null ? null : await ActorStore.LoadAsync(unitOfWork, userId.Value, now, cancellationToken);
        return profile is { Status: UserStatus.Active } && profile.Roles.Contains(BusinessRole.TechAdmin) ? profile : null;
    }
}
