using Rcs.Domain.Authorization;

namespace Rcs.Application.Identity;

public static class ActorAuthorityExtensions
{
    /// <summary>The authorization view of a profile: identity, status and the roles it holds now.</summary>
    public static ActorAuthority Authority(this ActorProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        return new ActorAuthority(profile.UserId, profile.Status, profile.Roles);
    }
}
