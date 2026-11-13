using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using PlexBridge.Data;
using PlexBridge.Plex;
using PlexBridge.Security;
using PlexBridge.Services;

namespace PlexBridge.Tests;

public class PlexServerServiceTests
{
    private const string Token = "stored-token-do-not-leak-88aa";

    [Fact]
    public async Task Encrypts_tokens_keeps_a_blank_edit_and_refreshes_read_only_counts()
    {
        using var fixture = new ServiceFixture();
        fixture.Handler.Responder = Respond;
        var service = fixture.Get<PlexServerService>();

        var saved = await service.SaveAsync(null, "Shelf", "http://plex.test:32400", Token);
        Assert.True(saved.HasToken);
        Assert.False(saved.TokenUnreadable);

        var kept = await service.SaveAsync(saved.Id, "Shelf renamed", "http://plex.test:32400", "  ");
        Assert.Equal("Shelf renamed", kept.Name);

        var refreshed = await service.RefreshInventoryAsync(saved.Id);
        var library = Assert.Single(refreshed.Libraries);
        Assert.Equal("Films", library.Title);
        Assert.Equal(3, library.ItemCount);
        Assert.Equal("/demo/films", Assert.Single(library.Locations));
        Assert.Equal("Online", refreshed.Status);
        Assert.DoesNotContain("ProtectedToken", JsonSerializer.Serialize(refreshed));
        Assert.DoesNotContain(Token, JsonSerializer.Serialize(refreshed));

        await using var scope = fixture.Provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var row = await db.PlexServers.SingleAsync();
        var protector = scope.ServiceProvider.GetRequiredService<ISecretProtector>();
        Assert.Equal(Token, protector.Unprotect(row.ProtectedToken!));
        Assert.NotEqual(Token, row.ProtectedToken);

        var replaced = await service.SaveAsync(saved.Id, "Shelf renamed", "http://plex.test:32400", "replacement-token-998877");
        Assert.True(replaced.HasToken);
        var rowAgain = await db.PlexServers.AsNoTracking().SingleAsync();
        Assert.Equal("replacement-token-998877", protector.Unprotect(rowAgain.ProtectedToken!));

        fixture.AssertNoSecret(Token);
        Assert.All(fixture.Handler.Requests, request => Assert.DoesNotContain("X-Plex-Token", request.Uri.Query, StringComparison.OrdinalIgnoreCase));
        Assert.Contains(fixture.Handler.Requests, request => request.Uri.AbsolutePath == "/library/sections");
        Assert.DoesNotContain(fixture.Handler.Requests, request => request.Uri.AbsolutePath.Contains("refresh", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Stops_at_two_servers_and_clears_inventory_when_the_url_changes()
    {
        using var fixture = new ServiceFixture();
        fixture.Handler.Responder = Respond;
        var service = fixture.Get<PlexServerService>();
        var first = await service.SaveAsync(null, "One", "http://plex.test:32400", Token);
        await service.RefreshInventoryAsync(first.Id);
        await service.SaveAsync(null, "Two", "http://plex.test:32401", Token);
        var error = await Assert.ThrowsAsync<PlexBridgeException>(() => service.SaveAsync(null, "Three", "http://plex.test:32402", Token));
        Assert.Contains("two", error.Message, StringComparison.OrdinalIgnoreCase);

        var moved = await service.SaveAsync(first.Id, "One", "http://other.test:32400", null);
        Assert.Empty(moved.Libraries);
        Assert.Equal("Not checked", moved.Status);
        fixture.AssertNoSecret(Token);
    }

    [Fact]
    public async Task Test_failure_does_not_surface_the_upstream_body()
    {
        using var fixture = new ServiceFixture();
        fixture.Handler.Responder = _ => HttpJson.Json("{\"detail\":\"" + Token + "\"}", System.Net.HttpStatusCode.Unauthorized);
        var service = fixture.Get<PlexServerService>();
        var saved = await service.SaveAsync(null, "Shelf", "http://plex.test:32400", Token);
        var result = await service.TestAsync(saved.Id, saved.BaseUrl, null);
        Assert.False(result.Ok);
        Assert.Equal("The server rejected the token.", result.Message);
        Assert.DoesNotContain(Token, result.Message);
        var listed = Assert.Single(await service.ListAsync());
        Assert.Equal("Token rejected", listed.Status);
        Assert.DoesNotContain(Token, listed.LastError ?? "");
        fixture.AssertNoSecret(Token);
    }

    [Fact]
    public async Task Migration_and_listing_keep_manual_tailscale_urls_tokens_and_slots()
    {
        const string nasToken = "nas-token-do-not-leak-11aa";
        const string desktopToken = "desktop-token-do-not-leak-22bb";
        const string nasUrl = "http://192.0.2.20:32400";
        const string desktopUrl = "http://100.64.8.2:32400";
        using var fixture = new ServiceFixture();
        Assert.Equal(nasUrl, PlexServerUrl.Normalize(nasUrl).Normalized);
        Assert.Equal(desktopUrl, PlexServerUrl.Normalize(desktopUrl).Normalized);

        await using var scope = fixture.Provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var protector = scope.ServiceProvider.GetRequiredService<ISecretProtector>();
        db.Database.Migrate();
        db.PlexServers.Add(new PlexServerConnection
        {
            Name = "NAS",
            BaseUrl = nasUrl,
            ProtectedToken = protector.Protect(nasToken),
            Slot = 1,
            Status = "Online",
            UpdatedUtc = DateTimeOffset.UtcNow
        });
        db.PlexServers.Add(new PlexServerConnection
        {
            Name = "Desktop",
            BaseUrl = desktopUrl,
            ProtectedToken = protector.Protect(desktopToken),
            Slot = 2,
            Status = "Online",
            UpdatedUtc = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();
        var before = await db.PlexServers.AsNoTracking().OrderBy(server => server.Slot).ToListAsync();

        db.Database.Migrate();
        var service = scope.ServiceProvider.GetRequiredService<PlexServerService>();
        var listed = await service.ListAsync();
        Assert.Equal(2, listed.Count);
        Assert.Equal(nasUrl, listed[0].BaseUrl);
        Assert.Equal(1, listed[0].Slot);
        Assert.Equal("NAS", listed[0].Name);
        Assert.Equal(desktopUrl, listed[1].BaseUrl);
        Assert.Equal(2, listed[1].Slot);
        Assert.Equal("Desktop", listed[1].Name);
        Assert.Empty(fixture.Handler.Requests);

        var after = await db.PlexServers.AsNoTracking().OrderBy(server => server.Slot).ToListAsync();
        Assert.Equal(before.Select(server => server.BaseUrl), after.Select(server => server.BaseUrl));
        Assert.Equal(before.Select(server => server.ProtectedToken), after.Select(server => server.ProtectedToken));
        Assert.Equal(before.Select(server => server.Slot), after.Select(server => server.Slot));
        Assert.Equal(nasToken, protector.Unprotect(after[0].ProtectedToken!));
        Assert.Equal(desktopToken, protector.Unprotect(after[1].ProtectedToken!));

        fixture.Handler.Responder = request =>
        {
            if (request.Uri.AbsolutePath is "/identity" or "/")
                return HttpJson.Json("""{"MediaContainer":{"machineIdentifier":"different-server","version":"9.9.9","friendlyName":"Other"}}""");
            if (request.Uri.AbsolutePath == "/library/sections")
                return HttpJson.Json("""{"MediaContainer":{"Directory":[]}}""");
            return HttpJson.Json("""{"MediaContainer":{"size":0,"totalSize":0,"Metadata":[]}}""");
        };
        var refreshed = await service.RefreshInventoryAsync(after[0].Id);
        Assert.Equal(nasUrl, refreshed.BaseUrl);
        Assert.Equal(1, refreshed.Slot);
        var kept = await db.PlexServers.AsNoTracking().SingleAsync(server => server.Id == after[0].Id);
        Assert.Equal(nasUrl, kept.BaseUrl);
        Assert.Equal(before[0].ProtectedToken, kept.ProtectedToken);
        Assert.Equal(1, kept.Slot);
        fixture.AssertNoSecret(nasToken);
        fixture.AssertNoSecret(desktopToken);
    }

    [Fact]
    public void Sensitive_data_logging_stays_off()
    {
        using var fixture = new ServiceFixture();
        using var scope = fixture.Provider.CreateScope();
        var options = scope.ServiceProvider.GetRequiredService<DbContextOptions<ApplicationDbContext>>();
        var extension = options.FindExtension<CoreOptionsExtension>();
        Assert.NotNull(extension);
        Assert.False(extension.IsSensitiveDataLoggingEnabled);
    }

    private static HttpResponseMessage Respond(CapturedRequest request)
    {
        if (request.Uri.AbsolutePath == "/identity")
            return HttpJson.Json("""{"MediaContainer":{"machineIdentifier":"m1","version":"1.0.0-test"}}""");
        if (request.Uri.AbsolutePath == "/")
            return HttpJson.Json("""{"MediaContainer":{"friendlyName":"Test Server","version":"1.0.0-test"}}""");
        if (request.Uri.AbsolutePath == "/library/sections")
            return HttpJson.Json("""{"MediaContainer":{"Directory":[{"key":"1","type":"movie","title":"Films","Location":[{"path":"/demo/films"}]}]}}""");
        if (request.Uri.AbsolutePath == "/library/sections/1/all")
            return HttpJson.Json("""{"MediaContainer":{"size":0,"totalSize":3,"offset":0,"Metadata":[]}}""");
        return HttpJson.Json("{}", System.Net.HttpStatusCode.NotFound);
    }
}

public class UserPreferenceTests
{
    [Fact]
    public async Task Migrate_view_is_saved_on_the_user_and_read_back()
    {
        var root = Path.Combine(Path.GetTempPath(), "plexbridge-prefs", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(root);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContextFactory<ApplicationDbContext>(options => options.UseSqlite($"Data Source={Path.Combine(root, "plexbridge.db")}"));
        services.AddIdentityCore<ApplicationUser>(options => options.User.RequireUniqueEmail = true)
            .AddRoles<IdentityRole>()
            .AddEntityFrameworkStores<ApplicationDbContext>();
        await using var provider = services.BuildServiceProvider();
        await using (var scope = provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            await db.Database.MigrateAsync();
        }

        string firstId;
        string secondId;
        await using (var scope = provider.CreateAsyncScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var first = new ApplicationUser { UserName = "one@localhost", Email = "one@localhost", EmailConfirmed = true };
            var second = new ApplicationUser { UserName = "two@localhost", Email = "two@localhost", EmailConfirmed = true };
            Assert.True((await users.CreateAsync(first, "Preview-check-2026!")).Succeeded);
            Assert.True((await users.CreateAsync(second, "Preview-check-2026!")).Succeeded);
            Assert.True((await users.AddClaimAsync(first, new Claim("plexbridge:keep", "yes"))).Succeeded);
            firstId = first.Id;
            secondId = second.Id;
            var preferences = new UserPreferences(users, new FixedAdminGate(firstId));
            Assert.Equal(UserPreferences.Mismatches, await preferences.GetMigrateViewAsync());
            await preferences.SetMigrateViewAsync(UserPreferences.ShowAll);
        }

        await using (var scope = provider.CreateAsyncScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var first = new UserPreferences(users, new FixedAdminGate(firstId));
            var second = new UserPreferences(users, new FixedAdminGate(secondId));
            Assert.Equal(UserPreferences.ShowAll, await first.GetMigrateViewAsync());
            Assert.Equal(UserPreferences.Mismatches, await second.GetMigrateViewAsync());
            var stored = await users.GetClaimsAsync(await users.FindByIdAsync(firstId) ?? throw new InvalidOperationException());
            Assert.Contains(stored, claim => claim.Type == "plexbridge:keep" && claim.Value == "yes");
            Assert.Contains(stored, claim => claim.Type == UserPreferences.MigrateViewClaim && claim.Value == UserPreferences.ShowAll);
            await first.SetMigrateViewAsync(UserPreferences.Mismatches);
        }

        await using (var scope = provider.CreateAsyncScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var first = new UserPreferences(users, new FixedAdminGate(firstId));
            Assert.Equal(UserPreferences.Mismatches, await first.GetMigrateViewAsync());
            var stored = await users.GetClaimsAsync(await users.FindByIdAsync(firstId) ?? throw new InvalidOperationException());
            Assert.Single(stored, claim => claim.Type == UserPreferences.MigrateViewClaim);
            Assert.Contains(stored, claim => claim.Type == "plexbridge:keep" && claim.Value == "yes");
        }
    }

    private sealed class FixedAdminGate(string userId) : IAdminGate
    {
        public Task<string> RequireUserIdAsync(CancellationToken cancellationToken) => Task.FromResult(userId);
    }
}
