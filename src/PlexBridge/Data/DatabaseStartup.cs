using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using PlexBridge.Services;

namespace PlexBridge.Data;

public static class DatabaseStartup
{
    public static async Task InitializeAsync(IServiceProvider services)
    {
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        await db.Database.MigrateAsync();
        await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;");
        await db.Database.ExecuteSqlRawAsync("PRAGMA busy_timeout=5000;");

        var roles = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>();
        if (!await roles.RoleExistsAsync(Auth.AdminRole))
            await roles.CreateAsync(new IdentityRole(Auth.AdminRole));
    }
}
