using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using PlexBridge.Data;

namespace PlexBridge.Services;

/// <summary>One admin account. Sign-up closes after the first account is created.</summary>
public sealed class AdminAccountService(UserManager<ApplicationUser> users, RoleManager<IdentityRole> roles)
{
    private static readonly SemaphoreSlim Gate = new(1, 1);

    public Task<bool> HasAccountAsync(CancellationToken cancellationToken = default)
        => users.Users.AnyAsync(cancellationToken);

    public async Task<IdentityResult> CreateAdminAsync(string email, string password, CancellationToken cancellationToken = default)
    {
        await Gate.WaitAsync(cancellationToken);
        try
        {
            if (await users.Users.AnyAsync(cancellationToken))
                return IdentityResult.Failed(new IdentityError { Code = "Closed", Description = "An admin account already exists." });

            var address = email.Trim();
            var user = new ApplicationUser { UserName = address, Email = address, EmailConfirmed = true };
            var created = await users.CreateAsync(user, password);
            if (!created.Succeeded)
                return created;

            if (!await roles.RoleExistsAsync(Auth.AdminRole))
            {
                var role = await roles.CreateAsync(new IdentityRole(Auth.AdminRole));
                if (!role.Succeeded)
                    return role;
            }

            return await users.AddToRoleAsync(user, Auth.AdminRole);
        }
        finally
        {
            Gate.Release();
        }
    }
}
