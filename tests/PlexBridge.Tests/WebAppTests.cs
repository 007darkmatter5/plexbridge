using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PlexBridge.Data;
using PlexBridge.Plex;
using PlexBridge.Security;

namespace PlexBridge.Tests;

public class WebAppTests : IClassFixture<PlexBridgeApp>
{
    private readonly PlexBridgeApp _app;

    public WebAppTests(PlexBridgeApp app) => _app = app;

    [Fact]
    public void Named_read_client_uses_the_20_second_timeout()
    {
        var factory = _app.Services.GetRequiredService<IHttpClientFactory>();
        var client = factory.CreateClient(PlexHttp.ClientName);
        Assert.Equal(TimeSpan.FromSeconds(20), client.Timeout);
    }

    [Fact]
    public void File_log_scrubs_tokens_in_urls()
    {
        var scrubbed = PlexBridge.Logging.RollingFileLoggerProvider.Scrub(
            "https://100.64.1.8:32400/:/scrobble?key=1&X-Plex-Token=source-token-do-not-leak-11");
        Assert.DoesNotContain("source-token-do-not-leak-11", scrubbed, StringComparison.Ordinal);
        Assert.DoesNotContain("?", scrubbed, StringComparison.Ordinal);
        Assert.Contains("https://100.64.1.8:32400/:/scrobble", scrubbed, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Health_is_anonymous_and_has_no_token()
    {
        using var client = _app.CreateClient();
        var response = await client.GetAsync("/healthz");
        var body = await response.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Healthy", body.Trim());
        Assert.DoesNotContain(PlexBridgeApp.Token, body);
    }

    [Fact]
    public async Task Anonymous_pages_do_not_echo_a_stored_token_or_inventory()
    {
        await _app.SeedServerAsync();
        using var client = _app.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = true });
        foreach (var path in new[] { "/", "/servers/1", "/account/password", "/healthz", "/Account/Login", "/Account/Register" })
        {
            var response = await client.GetAsync(path);
            var body = await response.Content.ReadAsStringAsync();
            Assert.DoesNotContain(PlexBridgeApp.Token, body);
            Assert.DoesNotContain("ProtectedToken", body);
            Assert.DoesNotContain("Marker Library Title", body);
        }
    }

    [Fact]
    public async Task Login_page_renders_and_logout_requires_antiforgery()
    {
        using var client = _app.CreateClient();
        var login = await client.GetAsync("/Account/Login");
        var html = await login.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        Assert.Contains("Log in", html);

        var logout = await client.PostAsync("/Account/Logout", new FormUrlEncodedContent(new Dictionary<string, string> { ["returnUrl"] = "https://evil.example" }));
        Assert.Equal(HttpStatusCode.BadRequest, logout.StatusCode);
    }

    [Fact]
    public async Task There_is_no_unauthenticated_plex_proxy()
    {
        using var client = _app.CreateClient();
        var response = await client.GetAsync("/api/plex?X-Plex-Token=" + PlexBridgeApp.Token);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.DoesNotContain(PlexBridgeApp.Token, body);
    }

    [Fact]
    public void Every_routed_page_is_authorized_or_explicitly_anonymous()
    {
        var routes = typeof(Program).Assembly.GetTypes()
            .Where(type => type.GetCustomAttributes(inherit: true).Any(attribute => attribute.GetType().Name == "RouteAttribute"))
            .ToList();
        Assert.NotEmpty(routes);
        foreach (var type in routes)
        {
            var anonymous = type.GetCustomAttributes(inherit: true).Any(attribute => attribute.GetType().Name == "AllowAnonymousAttribute");
            var authorize = type.GetCustomAttributes(inherit: true).Any(attribute => attribute.GetType().Name == "AuthorizeAttribute");
            Assert.True(anonymous ^ authorize, type.FullName);
        }
    }

    [Fact]
    public async Task Release_workflow_refuses_other_repositories()
    {
        var root = FindRoot();
        var release = await File.ReadAllTextAsync(Path.Combine(root, ".github", "workflows", "release.yml"));
        Assert.Contains("007darkmatter5/plexbridge", release);
        Assert.Contains("GITHUB_REPOSITORY", release);
        Assert.DoesNotContain("push: true", release.Split("Refuse to publish")[0]);
    }

    private static string FindRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "VERSION")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Repository root was not found.");
    }
}

public sealed class PlexBridgeApp : WebApplicationFactory<Program>
{
    public const string Token = "web-token-do-not-leak-42ff";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "plexbridge-web", Guid.NewGuid().ToString("n"));

    protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
    {
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(Path.Combine(_root, "keys"));
        builder.UseSetting("ConnectionStrings:DefaultConnection", "Data Source=" + Path.Combine(_root, "plexbridge.db"));
        builder.UseSetting("DataProtection:KeysPath", Path.Combine(_root, "keys"));
        builder.UseSetting(Microsoft.AspNetCore.Hosting.WebHostDefaults.EnvironmentKey, "Production");
    }

    public async Task SeedServerAsync()
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        await db.Database.MigrateAsync();
        if (await db.PlexServers.AnyAsync())
            return;
        var protector = scope.ServiceProvider.GetRequiredService<ISecretProtector>();
        var server = new PlexServerConnection
        {
            Name = "Seed",
            BaseUrl = "http://plex.test:32400",
            ProtectedToken = protector.Protect(Token),
            Status = ServerStatuses.Online,
            UpdatedUtc = DateTimeOffset.UtcNow
        };
        server.Libraries.Add(new LibrarySnapshot
        {
            SectionKey = "1",
            Title = "Marker Library Title",
            Type = "movie",
            ItemCount = 1,
            LocationsJson = "[]",
            RefreshedUtc = DateTimeOffset.UtcNow
        });
        db.PlexServers.Add(server);
        await db.SaveChangesAsync();
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        try { Directory.Delete(_root, true); } catch { /* best effort */ }
    }
}

public class ReleaseFileTests
{
    [Fact]
    public void Docker_and_version_files_match_the_container_contract()
    {
        var root = RepoRoot();
        var dockerfile = File.ReadAllText(Path.Combine(root, "Dockerfile"));
        Assert.Contains("EXPOSE 8080", dockerfile);
        Assert.Contains("Data Source=/config/plexbridge.db", dockerfile);
        Assert.Contains("DataProtection__KeysPath=/config/keys", dockerfile);
        Assert.Contains("HEALTHCHECK", dockerfile);
        Assert.Contains("PUID=1654", dockerfile);
        Assert.Contains("PGID=1654", dockerfile);
        Assert.DoesNotContain("RemoteCertificateValidationCallback", dockerfile);
        var entry = File.ReadAllText(Path.Combine(root, "docker", "entrypoint.sh"));
        Assert.Contains("Refusing to run PlexBridge as root", entry);
        Assert.Contains("PUID:-1654", entry);
        Assert.Contains("/config", entry);
        Assert.Equal("0.1", File.ReadAllText(Path.Combine(root, "VERSION")).Trim());
        var compose = File.ReadAllText(Path.Combine(root, "deploy", "unraid", "docker-compose.yml"));
        Assert.Contains("PUID: 99", compose);
        Assert.Contains("PGID: 100", compose);
        Assert.Contains("ghcr.io/007darkmatter5/plexbridge:latest", compose);
        Assert.Contains("/config", compose);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "VERSION")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Repository root was not found.");
    }
}
