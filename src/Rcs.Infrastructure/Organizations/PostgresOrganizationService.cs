using Npgsql;
using Rcs.Application.Common;
using Rcs.Application.Lookups;
using Rcs.Application.Organizations;
using Rcs.Domain.Authorization;
using Rcs.Domain.Vocabulary;
using Rcs.Infrastructure.Audit;
using Rcs.Infrastructure.Commands;
using Rcs.Infrastructure.Identity;
using Rcs.Infrastructure.Lookups;
using Rcs.Infrastructure.Persistence;

namespace Rcs.Infrastructure.Organizations;

internal sealed class PostgresOrganizationService(NpgsqlDataSource dataSource, CommandRunner runner) : IOrganizationService
{
    public async Task<IReadOnlyList<OrganizationListItem>> ListAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);

        await using var aliasCommand = new NpgsqlCommand(
            "SELECT organization_id, alias, alias_type, alias_language FROM rcs.organization_alias WHERE is_active", connection);
        var aliases = (await aliasCommand.ListAsync(
                reader => (Organization: reader.Uuid("organization_id"), Alias: new OrganizationAliasItem(reader.Text("alias"), reader.Text("alias_type"), reader.TextOrNull("alias_language"))),
                cancellationToken))
            .ToLookup(row => row.Organization, row => row.Alias);

        await using var command = new NpgsqlCommand("""
            SELECT o.id, o.official_name, o.short_name, t.code AS type_code, o.registration_code, o.is_own_organization, o.is_active, o.notes
            FROM rcs.organization AS o
            JOIN rcs.organization_type AS t ON t.id = o.organization_type_id
            """, connection);
        var organizations = await command.ListAsync(
            reader => new OrganizationListItem(
                reader.Uuid("id"),
                reader.Text("official_name"),
                reader.TextOrNull("short_name"),
                reader.Text("type_code"),
                reader.TextOrNull("registration_code"),
                reader.Bool("is_own_organization"),
                reader.Bool("is_active"),
                reader.TextOrNull("notes"),
                aliases[reader.Uuid("id")].ToArray()),
            cancellationToken);

        return organizations
            .OrderByDescending(organization => organization.IsOwnOrganization)
            .ThenBy(organization => organization.OfficialName, DisplayOrder.Comparer)
            .ToArray();
    }

    public async Task<IReadOnlyList<OrganizationOption>> ListActiveExternalAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            "SELECT id, official_name, short_name FROM rcs.organization WHERE is_active AND NOT is_own_organization", connection);
        var options = await command.ListAsync(
            reader => new OrganizationOption(reader.Uuid("id"), reader.TextOrNull("short_name") ?? reader.Text("official_name"), reader.Text("official_name")),
            cancellationToken);
        return options.OrderBy(option => option.Name, DisplayOrder.Comparer).ToArray();
    }

    public Task<CommandResult<Guid>> CreateAsync(ActorContext actor, CreateOrganizationCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        return runner.RunAsync(actor, "organization.create", operationId: null, caseId: null, async (scope, ct) =>
        {
            var id = scope.NewId();
            if (scope.Refuse(BusinessAction.CreateOrganization, null, AuditEntityTypes.Organization, id, null) is { } refused)
            {
                return refused;
            }

            var officialName = command.OfficialName?.Trim();
            if (string.IsNullOrEmpty(officialName))
            {
                return CommandResult<Guid>.Failure(CommandErrorKind.Validation, "validation.required", nameof(command.OfficialName));
            }

            var typeId = await PostgresLookupQueries.ActiveIdAsync(scope.UnitOfWork, LookupKind.OrganizationType, command.TypeCode ?? string.Empty, ct);
            if (typeId is null)
            {
                return CommandResult<Guid>.Failure(CommandErrorKind.Validation, "validation.required", nameof(command.TypeCode));
            }

            var shortName = Blank(command.ShortName);
            var registrationCode = Blank(command.RegistrationCode);
            await scope.UnitOfWork.Command("""
                    INSERT INTO rcs.organization (id, official_name, short_name, organization_type_id, registration_code, notes, created_by_user_id)
                    VALUES (@id, @official_name, @short_name, @type_id, @registration_code, @notes, @actor)
                    """)
                .With("id", id)
                .With("official_name", officialName)
                .With("short_name", shortName)
                .With("type_id", typeId)
                .With("registration_code", registrationCode)
                .With("notes", Blank(command.Notes))
                .With("actor", scope.Actor.UserId)
                .ExecuteAsync(ct);

            await scope.AuditAsync(new AuditEntry(AuditActionCodes.Create, AuditEntityTypes.Organization, id, 1, null,
                After: new { official_name = officialName, short_name = shortName, type = command.TypeCode, registration_code = registrationCode }), ct);

            return CommandResult<Guid>.Success(id);
        }, cancellationToken);
    }

    internal static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
