using Rcs.Application.Common;

namespace Rcs.Application.Organizations;

public sealed record OrganizationAliasItem(string Alias, string AliasType, string? Language);

public sealed record OrganizationListItem(
    Guid Id,
    string OfficialName,
    string? ShortName,
    string TypeCode,
    string? RegistrationCode,
    bool IsOwnOrganization,
    bool IsActive,
    string? Notes,
    IReadOnlyList<OrganizationAliasItem> Aliases);

/// <summary>An organization offered in a form. <see cref="Name"/> is the short name when there is one.</summary>
public sealed record OrganizationOption(Guid Id, string Name, string OfficialName);

public sealed record CreateOrganizationCommand(string OfficialName, string? ShortName, string TypeCode, string? RegistrationCode, string? Notes);

/// <summary>Organization master data (DOMAIN_MODEL.md §2.1, §2.2).</summary>
public interface IOrganizationService
{
    Task<IReadOnlyList<OrganizationListItem>> ListAsync(CancellationToken cancellationToken = default);

    /// <summary>Active external organizations — never the department itself.</summary>
    Task<IReadOnlyList<OrganizationOption>> ListActiveExternalAsync(CancellationToken cancellationToken = default);

    Task<CommandResult<Guid>> CreateAsync(ActorContext actor, CreateOrganizationCommand command, CancellationToken cancellationToken = default);
}
