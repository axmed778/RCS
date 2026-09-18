using Rcs.Domain.Vocabulary;

namespace Rcs.Application.Identity;

/// <summary>A user as the system knows them now, with roles evaluated at the moment of reading.</summary>
public sealed record ActorProfile(Guid UserId, string Username, string DisplayName, string? JobTitle, UserStatus Status, IReadOnlySet<BusinessRole> Roles);

public sealed record UserOption(Guid Id, string DisplayName, string? JobTitle);

public interface IUserDirectory
{
    Task<ActorProfile?> FindByUsernameAsync(string username, CancellationToken cancellationToken = default);

    Task<ActorProfile?> GetAsync(Guid userId, CancellationToken cancellationToken = default);

    /// <summary>Active users holding a business role (Worker, Chief or Head) — the people who can be responsible for a case.</summary>
    Task<IReadOnlyList<UserOption>> ListAssignableAsync(CancellationToken cancellationToken = default);
}
