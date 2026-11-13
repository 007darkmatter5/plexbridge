using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using PlexBridge.Data;
using PlexBridge.Security;

namespace PlexBridge.Services;

/// <summary>Per-admin preferences stored as Identity claims. No extra table.</summary>
public sealed class UserPreferences(UserManager<ApplicationUser> users, IAdminGate admin)
{
    public const string MigrateViewClaim = "plexbridge:migrate-view";
    public const string ShowAll = "all";
    public const string Mismatches = "mismatches";

    public async Task<string> GetMigrateViewAsync(CancellationToken cancellationToken = default)
    {
        var user = await CurrentUserAsync(cancellationToken);
        var claim = (await users.GetClaimsAsync(user)).FirstOrDefault(item => item.Type == MigrateViewClaim);
        return claim?.Value == ShowAll ? ShowAll : Mismatches;
    }

    public async Task SetMigrateViewAsync(string view, CancellationToken cancellationToken = default)
    {
        var value = view == ShowAll ? ShowAll : Mismatches;
        var user = await CurrentUserAsync(cancellationToken);
        var existing = (await users.GetClaimsAsync(user)).Where(item => item.Type == MigrateViewClaim).ToList();
        if (existing.Count == 1 && existing[0].Value == value)
            return;
        if (existing.Count > 0)
        {
            var removed = await users.RemoveClaimsAsync(user, existing);
            if (!removed.Succeeded)
                throw new PlexBridgeException("The view preference could not be saved.");
        }

        var added = await users.AddClaimAsync(user, new Claim(MigrateViewClaim, value));
        if (!added.Succeeded)
            throw new PlexBridgeException("The view preference could not be saved.");
    }

    private async Task<ApplicationUser> CurrentUserAsync(CancellationToken cancellationToken)
    {
        var userId = await admin.RequireUserIdAsync(cancellationToken);
        var user = await users.FindByIdAsync(userId);
        if (user is null)
            throw new PlexBridgeException("The signed-in account is no longer available.");
        return user;
    }
}
