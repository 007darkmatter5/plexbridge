using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Identity;
using PlexBridge.Data;
using PlexBridge.Security;

namespace PlexBridge.Services;

public interface IAdminGate
{
    Task<string> RequireUserIdAsync(CancellationToken cancellationToken);
}

public sealed class AuthenticationAdminGate(AuthenticationStateProvider authentication, UserManager<ApplicationUser> users) : IAdminGate
{
    public async Task<string> RequireUserIdAsync(CancellationToken cancellationToken)
    {
        var state = await authentication.GetAuthenticationStateAsync();
        var user = await users.GetUserAsync(state.User);
        if (user is null || !await users.IsInRoleAsync(user, Auth.AdminRole))
            throw new PlexBridgeException("Only the admin can connect a Plex account.");
        return user.Id;
    }
}
