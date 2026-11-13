using System.Diagnostics;
using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PlexBridge.Data;
using PlexBridge.Plex;
using PlexBridge.Security;
using PlexBridge.Services;

namespace PlexBridge.Tests;

public class PlexAccountFlowTests
{
    private const string AccountToken = "account-token-do-not-leak-77bb";
    private const string ServerToken = "server-token-do-not-leak-66aa";
    private const string PinCode = "pin-code-do-not-leak";

    [Fact]
    public void Authorize_url_has_the_pin_fragment_and_no_account_token()
    {
        var url = PlexAuthorizeUrl.Build("client-1", PinCode);
        Assert.StartsWith("https://app.plex.tv/auth#?", url);
        Assert.Contains("code=", url, StringComparison.Ordinal);
        Assert.DoesNotContain("authToken", url, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(AccountToken, url);
    }

    [Fact]
    public void Auth_policy_is_separate_from_the_media_client()
    {
        Assert.True(PlexAuthPolicy.IsAllowed(HttpMethod.Post, new Uri("https://plex.tv/api/v2/pins?strong=true")));
        Assert.True(PlexAuthPolicy.IsAllowed(HttpMethod.Get, new Uri("https://plex.tv/api/v2/pins/42")));
        Assert.True(PlexAuthPolicy.IsAllowed(HttpMethod.Get, new Uri("https://plex.tv/api/v2/user")));
        Assert.True(PlexAuthPolicy.IsAllowed(HttpMethod.Get, new Uri("https://plex.tv/api/v2/resources?includeHttps=1&includeRelay=1")));
        Assert.False(PlexAuthPolicy.IsAllowed(HttpMethod.Post, new Uri("https://plex.tv/api/v2/user")));
        Assert.False(PlexAuthPolicy.IsAllowed(HttpMethod.Get, new Uri("https://plex.tv/api/v2/resources?X-Plex-Token=" + AccountToken)));
        Assert.False(PlexAuthPolicy.IsAllowed(HttpMethod.Get, new Uri("https://evil.example/api/v2/pins")));
        Assert.False(PlexAuthPolicy.IsAllowed(HttpMethod.Get, new Uri("http://plex.tv/api/v2/user")));
        Assert.False(PlexReadOnlyPolicy.IsAllowed(HttpMethod.Post, "/api/v2/pins"));
    }

    [Fact]
    public async Task Media_client_refuses_plex_tv()
    {
        var handler = new ScriptHandler();
        var client = new PlexReadClient(new HandlerTransport(handler), new AllowAllAddressGuard());
        var ex = await Assert.ThrowsAsync<PlexRequestException>(() =>
            client.ReadForTestsAsync(new Uri("https://plex.tv/"), AccountToken, "/identity", CancellationToken.None));
        Assert.Empty(handler.Requests);
        Assert.DoesNotContain(AccountToken, ex.Message);
    }

    [Fact]
    public void Discovery_keeps_owned_servers_and_drops_tokens_from_the_view()
    {
        var owned = PlexAuthJson.ParseOwnedServers("""
            [
              {
                "name": "Desktop",
                "productVersion": "1.43.5",
                "provides": "server",
                "clientIdentifier": "machine-desktop",
                "owned": true,
                "accessToken": "server-token-do-not-leak-66aa",
                "httpsRequired": false,
                "connections": [
                  {"protocol":"https","uri":"https://10-1-1-5.abcdef.plex.direct:32400","local":true,"relay":false},
                  {"protocol":"http","uri":"http://10.1.1.5:32400","local":true,"relay":false},
                  {"protocol":"https","uri":"https://relay.example:443","local":false,"relay":true}
                ]
              },
              {"name":"Player","provides":"player","owned":true,"accessToken":"player-token","clientIdentifier":"player"},
              {"name":"Shared","provides":"server","owned":false,"accessToken":"shared-token","clientIdentifier":"shared"}
            ]
            """);
        var server = Assert.Single(owned);
        Assert.Equal(ServerToken, server.AccessToken);
        var view = Assert.Single(PlexAuthJson.WithoutSecrets(owned));
        var json = JsonSerializer.Serialize(view);
        Assert.DoesNotContain(ServerToken, json);
        Assert.DoesNotContain("shared-token", json);
        var ordered = PlexConnectionOrder.Order(server.Connections);
        Assert.Equal(3, ordered.Count);
        Assert.Equal("https://10-1-1-5.abcdef.plex.direct:32400", ordered[0].Uri);
        Assert.Contains(ordered, candidate => candidate.Uri.StartsWith("http://", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Pin_flow_encrypts_the_account_token_and_honors_expiry_backoff_and_admin()
    {
        await using var fixture = new AccountFixture();
        var polls = 0;
        fixture.Handler.Responder = request =>
        {
            if (request.Method == HttpMethod.Post && request.Uri.AbsolutePath == "/api/v2/pins")
                return HttpJson.Json($$"""{"id":99,"code":"{{PinCode}}","authToken":null,"expiresIn":1800}""");
            if (request.Uri.AbsolutePath == "/api/v2/pins/99")
            {
                polls++;
                return HttpJson.Json(polls == 1
                    ? """{"id":99,"authToken":null}"""
                    : $$"""{"id":99,"authToken":"{{AccountToken}}"}""");
            }
            if (request.Uri.AbsolutePath == "/api/v2/user")
                return HttpJson.Json("""{"id":5,"username":"household","email":"person@example.com"}""");
            return HttpJson.Json("{}", HttpStatusCode.NotFound);
        };

        var started = await fixture.Service.StartPinAsync();
        Assert.StartsWith("https://app.plex.tv/auth#?", started.AuthorizeUrl);
        Assert.DoesNotContain(AccountToken, started.AuthorizeUrl);
        Assert.Contains(PinCode, started.AuthorizeUrl);

        var pending = await fixture.Service.PollPinAsync();
        Assert.Equal(PlexPinState.Pending, pending.State);
        var tooSoon = await fixture.Service.PollPinAsync();
        Assert.Equal(PlexPinState.Pending, tooSoon.State);
        Assert.Equal(1, fixture.Handler.Requests.Count(request => request.Uri.AbsolutePath == "/api/v2/pins/99"));

        fixture.Clock.Utc = fixture.Clock.Utc.AddSeconds(3);
        var authorized = await fixture.Service.PollPinAsync();
        Assert.Equal(PlexPinState.Authorized, authorized.State);
        Assert.Equal("household", authorized.Link?.Username);
        Assert.DoesNotContain(AccountToken, authorized.Message);
        Assert.All(fixture.Handler.Requests, request => Assert.DoesNotContain(AccountToken, request.Uri.ToString()));

        await using var scope = fixture.Provider.CreateAsyncScope();
        var row = await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().PlexAccountLinks.SingleAsync();
        Assert.NotEqual(AccountToken, row.ProtectedAccountToken);
        Assert.Equal(AccountToken, scope.ServiceProvider.GetRequiredService<ISecretProtector>().Unprotect(row.ProtectedAccountToken!));
        Assert.DoesNotContain("person@example.com", JsonSerializer.Serialize(row));
        fixture.AssertNoSecret(AccountToken);
        fixture.AssertNoSecret(PinCode);
    }

    [Fact]
    public async Task Expired_pin_does_not_call_plex_again()
    {
        await using var fixture = new AccountFixture();
        fixture.Handler.Responder = _ => HttpJson.Json($$"""{"id":7,"code":"{{PinCode}}","expiresAt":"2000-01-01T00:00:00Z"}""");
        await fixture.Service.StartPinAsync();
        var calls = fixture.Handler.Requests.Count;
        var expired = await fixture.Service.PollPinAsync();
        Assert.Equal(PlexPinState.Expired, expired.State);
        Assert.Equal(calls, fixture.Handler.Requests.Count);
        Assert.DoesNotContain(PinCode, expired.Message);
    }

    [Fact]
    public async Task Non_admin_cannot_start_or_discover()
    {
        await using var fixture = new AccountFixture { Admin = false };
        var ex = await Assert.ThrowsAsync<PlexBridgeException>(() => fixture.Service.StartPinAsync());
        Assert.Empty(fixture.Handler.Requests);
        Assert.DoesNotContain(AccountToken, ex.Message);
        await Assert.ThrowsAsync<PlexBridgeException>(() => fixture.Service.DiscoverAsync());
        Assert.Empty(fixture.Handler.Requests);
    }

    [Fact]
    public async Task Checks_report_each_address_before_the_next_and_keep_a_later_success()
    {
        var session = ConnectionCheckSession.Create("machine-1",
            [
                new PlexConnectionCandidate("https://a.example:32400", true, false, true),
                new PlexConnectionCandidate("https://b.example:32400", true, false, true)
            ],
            httpsRequired: false);
        var snapshots = new List<string>();
        var calls = 0;
        await ConnectionCheckRunner.RunAsync(session, async (uri, _) =>
        {
            calls++;
            snapshots.Add(string.Join(",", session.Rows.Select(row => row.Status)));
            await Task.Yield();
            return uri.Contains("//a.", StringComparison.Ordinal)
                ? new ProbeAttemptView(uri, "Timed out", "The server did not respond before the timeout.")
                : new ProbeAttemptView(uri, "Reachable", "Read-only identity check succeeded.");
        }, () =>
        {
            snapshots.Add(string.Join(",", session.Rows.Select(row => row.Status)));
            return Task.CompletedTask;
        }, CancellationToken.None);

        Assert.Equal(2, calls);
        Assert.Contains("Testing,Queued", snapshots);
        Assert.Contains("Failed,Queued", snapshots);
        Assert.Contains("Failed,Testing", snapshots);
        Assert.Equal(ConnectionCheckStatus.Failed, session.Rows[0].Status);
        Assert.Equal(ConnectionCheckStatus.Verified, session.Rows[1].Status);
        Assert.Contains("timeout", session.Rows[0].Detail, StringComparison.OrdinalIgnoreCase);
        Assert.True(session.AnyVerified);
        Assert.False(session.Running);
    }

    [Fact]
    public async Task Cancel_leaves_the_rest_unchecked()
    {
        var session = ConnectionCheckSession.Create("machine-1",
            [
                new PlexConnectionCandidate("https://a.example:32400", true, false, true),
                new PlexConnectionCandidate("https://b.example:32400", true, false, true)
            ],
            httpsRequired: false);
        using var cts = new CancellationTokenSource();
        var calls = 0;
        await ConnectionCheckRunner.RunAsync(session, (uri, token) =>
        {
            calls++;
            cts.Cancel();
            return Task.FromResult(new ProbeAttemptView(uri, "Unreachable", "Could not connect to the server."));
        }, () => Task.CompletedTask, cts.Token);

        Assert.Equal(1, calls);
        Assert.Equal(ConnectionCheckStatus.Failed, session.Rows[0].Status);
        Assert.Equal(ConnectionCheckStatus.Canceled, session.Rows[1].Status);
    }

    [Fact]
    public async Task Saving_a_verified_address_uses_that_servers_token_and_slot()
    {
        await using var fixture = new AccountFixture();
        await fixture.LinkAccountAsync(AccountToken);
        fixture.Handler.Responder = request =>
        {
            if (request.Uri.AbsolutePath == "/api/v2/resources")
            {
                return HttpJson.Json($$"""
                    [
                      {"name":"Shelf","provides":"server","clientIdentifier":"machine-1","owned":true,"accessToken":"{{ServerToken}}","connections":[
                        {"protocol":"https","uri":"https://shelf.example:32400","local":true,"relay":false},
                        {"protocol":"https","uri":"https://shelf-relay.example:443","local":false,"relay":true}
                      ]},
                      {"name":"Other","provides":"server","clientIdentifier":"machine-2","owned":true,"accessToken":"other-server-token-99","connections":[
                        {"protocol":"https","uri":"https://other.example:32400","local":true,"relay":false}
                      ]}
                    ]
                    """);
            }
            return HttpJson.Json("{}", HttpStatusCode.NotFound);
        };
        fixture.Media.Responder = request =>
        {
            if (request.Uri.Host == "shelf.example")
                return HttpJson.Json("""{"MediaContainer":{"machineIdentifier":"machine-1","version":"1.2.3"}}""");
            return new HttpResponseMessage(HttpStatusCode.Unauthorized);
        };

        var uris = await fixture.Service.CandidatesAsync("machine-1");
        Assert.Equal("https://shelf.example:32400", uris[0].Uri);
        var failed = await fixture.Service.ProbeCandidateAsync("machine-1", uris[1].Uri);
        Assert.NotEqual("Reachable", failed.Outcome);
        var verified = await fixture.Service.ProbeCandidateAsync("machine-1", uris[0].Uri);
        Assert.Equal("Reachable", verified.Outcome);

        await Assert.ThrowsAsync<PlexBridgeException>(() => fixture.Service.SaveDiscoveredAsync("machine-1", "https://other.example:32400", null, "Shelf", 2));
        var saved = await fixture.Service.SaveDiscoveredAsync("machine-1", verified.Uri, null, "Shelf", 2);
        Assert.Equal("https://shelf.example:32400", saved.BaseUrl);
        Assert.Equal("Shelf", saved.Name);
        Assert.Equal(2, saved.Slot);

        await using var scope = fixture.Provider.CreateAsyncScope();
        var row = await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().PlexServers.SingleAsync();
        var token = scope.ServiceProvider.GetRequiredService<ISecretProtector>().Unprotect(row.ProtectedToken!);
        Assert.Equal(ServerToken, token);
        Assert.NotEqual(AccountToken, token);
        Assert.DoesNotContain("other-server-token-99", token);
        fixture.AssertNoSecret(ServerToken);
    }

    [Fact]
    public async Task Nine_candidates_stay_listed_and_a_later_one_can_be_saved()
    {
        await using var fixture = new AccountFixture();
        await fixture.LinkAccountAsync(AccountToken);
        var addresses = new[]
        {
            "https://172-18-0-1.preview.plex.direct:32400",
            "https://172-22-0-1.preview.plex.direct:32400",
            "https://172-26-0-1.preview.plex.direct:32400",
            "https://172-19-0-1.preview.plex.direct:32400",
            "https://172-17-0-1.preview.plex.direct:32400",
            "https://10-20-51-142.preview.plex.direct:32400",
            "https://10-20-51-141.preview.plex.direct:32400",
            "https://192-0-2-10.preview.plex.direct:32401",
            "https://198-51-100-5.preview.plex.direct:8443"
        };
        fixture.Handler.Responder = _ => HttpJson.Json(Resources(addresses));
        fixture.Media.Responder = request =>
            request.Uri.Host.StartsWith("10-20-51-", StringComparison.Ordinal)
                ? HttpJson.Json("""{"MediaContainer":{"machineIdentifier":"machine-1","version":"1.0"}}""")
                : new HttpResponseMessage(HttpStatusCode.RequestTimeout);

        var session = await fixture.Service.BeginCheckAsync("machine-1");
        Assert.Equal(9, session.Rows.Count);
        Assert.Equal(9, addresses.Count(address => session.Rows.Any(row => row.Uri == address)));
        Assert.DoesNotContain(session.Rows, row => row.Status == ConnectionCheckStatus.NotTested);
        var seen = new List<int>();
        await ConnectionCheckRunner.RunAsync(session, (uri, _) =>
        {
            seen.Add(session.Rows.Count(row => row.Status == ConnectionCheckStatus.Testing));
            var outcome = uri.Contains("10-20-51-", StringComparison.Ordinal) ? "Reachable" : "Timed out";
            var detail = outcome == "Reachable" ? "Read-only identity check succeeded." : "The server did not respond before the timeout.";
            return Task.FromResult(new ProbeAttemptView(uri, outcome, detail));
        }, () => Task.CompletedTask, CancellationToken.None);

        Assert.Equal(9, seen.Count);
        Assert.Equal(9, session.Rows.Count);
        Assert.Equal(2, session.Rows.Count(row => row.Status == ConnectionCheckStatus.Verified));
        Assert.Contains(session.Rows, row => row.Uri == addresses[5] && row.Status == ConnectionCheckStatus.Verified);
        Assert.Contains(session.Rows, row => row.Uri == addresses[0] && row.Status == ConnectionCheckStatus.Failed);
        var saved = await fixture.Service.SaveDiscoveredAsync("machine-1", addresses[6], null, "Shelf", 1);
        Assert.Equal(addresses[6], saved.BaseUrl);
        Assert.Equal(1, saved.Slot);
    }

    [Fact]
    public void More_than_the_automatic_batch_stays_visible_as_not_tested()
    {
        var candidates = Enumerable.Range(1, 20)
            .Select(index => new PlexConnectionCandidate($"https://10-0-0-{index}.preview.plex.direct:32400", true, false, true))
            .ToList();
        var session = ConnectionCheckSession.Create("machine-1", candidates, httpsRequired: false);
        Assert.Equal(20, session.Rows.Count);
        Assert.Equal(PlexConnectionOrder.AutomaticBatchSize, session.Rows.Count(row => row.Status == ConnectionCheckStatus.Queued));
        Assert.Equal(4, session.Rows.Count(row => row.Status == ConnectionCheckStatus.NotTested));
        Assert.Contains("Not checked automatically", session.Rows[^1].Detail);
    }

    private static string Resources(IEnumerable<string> uris)
    {
        var connections = string.Join(",", uris.Select(uri =>
            $$"""{"protocol":"https","uri":"{{uri}}","local":true,"relay":false}"""));
        return $$"""[{"name":"Shelf","provides":"server","clientIdentifier":"machine-1","owned":true,"accessToken":"{{ServerToken}}","connections":[{{connections}}]}]""";
    }

    [Fact]
    public async Task Probe_reports_certificate_and_token_failures_without_the_token()
    {
        await using var fixture = new AccountFixture();
        await fixture.LinkAccountAsync(AccountToken);
        fixture.Handler.Responder = request =>
        {
            if (request.Uri.AbsolutePath == "/api/v2/resources")
            {
                return HttpJson.Json($$"""
                    [{
                      "name":"Shelf","provides":"server","clientIdentifier":"machine-1","owned":1,
                      "accessToken":"{{ServerToken}}","httpsRequired":true,
                      "connections":[
                        {"protocol":"https","uri":"https://server.example:32400","local":true,"relay":false},
                        {"protocol":"http","uri":"http://10.1.1.9:32400","local":true,"relay":false}
                      ]
                    }]
                    """);
            }
            return HttpJson.Json("{}", HttpStatusCode.NotFound);
        };
        fixture.Media.Responder = request => new HttpResponseMessage(HttpStatusCode.Unauthorized)
        {
            Content = new StringContent(ServerToken)
        };
        var session = await fixture.Service.BeginCheckAsync("machine-1");
        Assert.Equal(2, session.Rows.Count);
        Assert.Contains(session.Rows, row => row.Uri.StartsWith("http://", StringComparison.Ordinal) && row.Status == ConnectionCheckStatus.NotTested);
        var probe = await fixture.Service.ProbeAsync("machine-1");
        Assert.Equal(2, probe.Attempts.Count);
        Assert.All(probe.Attempts, attempt => Assert.Equal("Token rejected", attempt.Outcome));
        Assert.All(probe.Attempts, attempt => Assert.DoesNotContain(ServerToken, attempt.Detail));
        Assert.DoesNotContain(ServerToken, JsonSerializer.Serialize(probe));
        Assert.False(probe.Connected);
    }

    [Fact]
    public async Task Hung_probe_ends_as_failed_and_a_verified_row_hides_the_banner()
    {
        var session = ConnectionCheckSession.Create("machine-1",
        [
            new PlexConnectionCandidate("https://good.example:32400", false, false, true),
            new PlexConnectionCandidate("https://relay.example:8443", false, true, true)
        ], httpsRequired: false);
        session.ProbeLimit = TimeSpan.FromMilliseconds(200);
        var started = Stopwatch.StartNew();
        await ConnectionCheckRunner.RunAsync(session, async (uri, _) =>
        {
            if (!uri.Contains("relay", StringComparison.Ordinal))
                return new ProbeAttemptView(uri, "Reachable", "Read-only identity check succeeded.");
            await Task.Delay(TimeSpan.FromSeconds(5));
            return new ProbeAttemptView(uri, "Reachable", "late");
        }, () => Task.CompletedTask, CancellationToken.None);

        Assert.True(started.Elapsed < TimeSpan.FromSeconds(2), started.Elapsed.ToString());
        Assert.Equal(ConnectionCheckStatus.Verified, session.Rows[0].Status);
        Assert.Equal(ConnectionCheckStatus.Failed, session.Rows[1].Status);
        Assert.DoesNotContain(session.Rows, row => row.Status == ConnectionCheckStatus.Testing);
        Assert.False(session.Running);
        Assert.True(session.AnyVerified);
        Assert.False(session.ShowNoneAnswered);
        await Task.Delay(TimeSpan.FromMilliseconds(400));
        Assert.Equal(ConnectionCheckStatus.Failed, session.Rows[1].Status);
    }

    [Fact]
    public void None_answered_stays_hidden_while_a_row_is_still_testing()
    {
        var session = ConnectionCheckSession.Create("machine-1",
        [
            new PlexConnectionCandidate("https://relay.example:8443", false, true, true)
        ], httpsRequired: false);
        session.Running = false;
        session.Rows[0].Status = ConnectionCheckStatus.Testing;
        Assert.False(session.ShowNoneAnswered);
        session.Rows[0].Status = ConnectionCheckStatus.Failed;
        Assert.True(session.ShowNoneAnswered);
    }

    [Fact]
    public async Task Probe_exception_does_not_leave_the_row_testing()
    {
        var session = ConnectionCheckSession.Create("machine-1",
        [
            new PlexConnectionCandidate("https://relay.example:8443", false, true, true)
        ], httpsRequired: false);
        await ConnectionCheckRunner.RunAsync(
            session,
            (_, _) => throw new InvalidOperationException("boom"),
            () => Task.CompletedTask,
            CancellationToken.None);
        Assert.Equal(ConnectionCheckStatus.Failed, Assert.Single(session.Rows).Status);
        Assert.False(session.Running);
        Assert.True(session.ShowNoneAnswered);
    }

    [Fact]
    public async Task Cancel_finishes_the_check_promptly()
    {
        var session = ConnectionCheckSession.Create("machine-1",
        [
            new PlexConnectionCandidate("https://relay.example:8443", false, true, true),
            new PlexConnectionCandidate("https://other.example:32400", false, false, true)
        ], httpsRequired: false);
        session.ProbeLimit = TimeSpan.FromSeconds(8);
        using var cts = new CancellationTokenSource();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var run = ConnectionCheckRunner.RunAsync(session, async (_, token) =>
        {
            started.TrySetResult();
            await Task.Delay(TimeSpan.FromSeconds(30), token);
            return new ProbeAttemptView("https://relay.example:8443", "Reachable", "late");
        }, () => Task.CompletedTask, cts.Token);

        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        cts.Cancel();
        await run.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(ConnectionCheckStatus.Canceled, session.Rows[0].Status);
        Assert.Equal(ConnectionCheckStatus.Canceled, session.Rows[1].Status);
        Assert.False(session.Running);
        Assert.False(session.ShowNoneAnswered);
    }

    [Fact]
    public async Task Cancel_does_not_wait_for_a_probe_that_ignores_cancellation()
    {
        var session = ConnectionCheckSession.Create("machine-1",
        [
            new PlexConnectionCandidate("https://relay.example:8443", false, true, true)
        ], httpsRequired: false);
        session.ProbeLimit = TimeSpan.FromSeconds(8);
        using var cts = new CancellationTokenSource();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var run = ConnectionCheckRunner.RunAsync(session, async (_, _) =>
        {
            started.TrySetResult();
            await Task.Delay(TimeSpan.FromSeconds(30));
            return new ProbeAttemptView("https://relay.example:8443", "Reachable", "late");
        }, () => Task.CompletedTask, cts.Token);

        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        cts.Cancel();
        var startedWait = Stopwatch.StartNew();
        await run.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(startedWait.Elapsed < TimeSpan.FromSeconds(2));
        Assert.Equal(ConnectionCheckStatus.Canceled, session.Rows[0].Status);
        Assert.False(session.Running);
        await Task.Delay(TimeSpan.FromMilliseconds(300));
        Assert.Equal(ConnectionCheckStatus.Canceled, session.Rows[0].Status);
    }

    [Fact]
    public async Task Probe_returns_when_the_server_ignores_cancellation()
    {
        await using var fixture = new AccountFixture();
        fixture.Limits.Limit = TimeSpan.FromMilliseconds(250);
        await fixture.LinkAccountAsync(AccountToken);
        fixture.Handler.Responder = _ => HttpJson.Json(Resources(["https://slow.example:32400"]));
        fixture.Media.AsyncResponder = async (_, _) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(5));
            return HttpJson.Json("""{"MediaContainer":{"machineIdentifier":"machine-1","version":"1.0"}}""");
        };

        var started = Stopwatch.StartNew();
        var attempt = await fixture.Service.ProbeCandidateAsync("machine-1", "https://slow.example:32400");
        Assert.True(started.Elapsed < TimeSpan.FromSeconds(2), started.Elapsed.ToString());
        Assert.Equal("Timed out", attempt.Outcome);
        Assert.DoesNotContain(ServerToken, attempt.Detail);
        Assert.DoesNotContain(AccountToken, attempt.Detail);
        fixture.AssertNoSecret(ServerToken);
    }
}

public sealed class AccountFixture : IAsyncDisposable
{
    public ScriptHandler Handler { get; } = new();
    public ScriptHandler Media { get; } = new();
    public ManualClock Clock { get; } = new();
    public PlexProbeLimits Limits { get; } = new();
    public ServiceProvider Provider { get; }
    public PlexAccountService Service { get; }
    private readonly Gate _gate = new();
    public bool Admin
    {
        init => _gate.Allow = value;
    }
    private readonly string _root = Path.Combine(Path.GetTempPath(), "plexbridge-account", Guid.NewGuid().ToString("n"));

    public AccountFixture()
    {
        Directory.CreateDirectory(_root);
        var keys = Path.Combine(_root, "keys");
        Directory.CreateDirectory(keys);
        var services = new ServiceCollection();
        var logs = new SpyLoggerProvider();
        services.AddLogging(builder => builder.AddProvider(logs).SetMinimumLevel(Microsoft.Extensions.Logging.LogLevel.Trace));
        services.AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo(keys)).SetApplicationName("PlexBridgeAccountTests");
        services.AddSingleton<ISecretProtector, DataProtectionSecretProtector>();
        services.AddDbContextFactory<ApplicationDbContext>(options => options.UseSqlite($"Data Source={Path.Combine(_root, "plexbridge.db")}"));
        services.AddSingleton<IPlexAuthTransport>(new AuthTransport(Handler));
        services.AddSingleton<PlexAuthClient>();
        services.AddSingleton<IPlexTransport>(new HandlerTransport(Media));
        services.AddSingleton<IPlexAddressGuard, AllowAllAddressGuard>();
        services.AddSingleton<PlexReadClient>();
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<TimeProvider>(Clock);
        services.AddSingleton<PlexPinCoordinator>();
        services.AddSingleton(Limits);
        services.AddScoped<PlexServerService>();
        services.AddSingleton<IAdminGate>(_gate);
        services.AddScoped<PlexAccountService>();
        Provider = services.BuildServiceProvider();
        using var scope = Provider.CreateScope();
        scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().Database.Migrate();
        Service = Provider.CreateScope().ServiceProvider.GetRequiredService<PlexAccountService>();
        Logs = logs;
    }

    public SpyLoggerProvider Logs { get; }

    public async Task LinkAccountAsync(string token)
    {
        await using var scope = Provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        db.PlexAccountLinks.Add(new PlexAccountLink
        {
            UserId = "admin",
            ClientIdentifier = "client-test",
            ProtectedAccountToken = scope.ServiceProvider.GetRequiredService<ISecretProtector>().Protect(token),
            Username = "household",
            UpdatedUtc = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();
    }

    public void AssertNoSecret(string secret)
    {
        Assert.DoesNotContain(Logs.Logger.Messages, message => message.Contains(secret, StringComparison.Ordinal));
    }

    public async ValueTask DisposeAsync()
    {
        await Provider.DisposeAsync();
        try { Directory.Delete(_root, true); } catch { }
    }

    private sealed class Gate : IAdminGate
    {
        public bool Allow { get; set; } = true;
        public Task<string> RequireUserIdAsync(CancellationToken cancellationToken)
            => Allow ? Task.FromResult("admin") : throw new PlexBridgeException("Only the admin can connect a Plex account.");
    }

    private sealed class AuthTransport(ScriptHandler handler) : IPlexAuthTransport
    {
        private readonly HttpClient _client = new(handler, disposeHandler: false);
        public Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => _client.SendAsync(request, cancellationToken);
    }
}

public sealed class ManualClock : TimeProvider
{
    public DateTimeOffset Utc { get; set; } = new(2026, 11, 6, 12, 0, 0, TimeSpan.Zero);
    public override DateTimeOffset GetUtcNow() => Utc;
}
