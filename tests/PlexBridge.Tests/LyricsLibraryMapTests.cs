using System.Text;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using PlexBridge.Data;
using PlexBridge.Lyrics;
using PlexBridge.Plex;
using PlexBridge.Security;
using PlexBridge.Services;

namespace PlexBridge.Tests;

public class LyricsLibraryMapTests
{
    [Fact]
    public void Maps_a_single_folder_when_the_tracks_are_directly_under_the_mount()
    {
        var probes = new List<string>();
        var ok = LyricsLibraryMapper.TryMatch(
            ["/mnt/user/media/music"],
            "/music",
            ["/mnt/user/media/music/Artist/Album/01.flac"],
            path =>
            {
                probes.Add(path);
                return false;
            },
            path => path == "/music/Artist/Album/01.flac",
            out var matches,
            out var maps,
            out var error);

        Assert.True(ok, error);
        var match = Assert.Single(matches);
        Assert.Equal(LyricsMapStatus.Ok, match.Status);
        Assert.Equal("/music", match.ContainerPrefix);
        Assert.Equal(1, match.SamplesFound);
        Assert.Equal("/mnt/user/media/music", Assert.Single(maps).PlexPrefix);
        Assert.Equal("/music", Assert.Single(maps).ContainerPrefix);
        Assert.Equal(["/music/music"], probes);
    }

    [Fact]
    public void Maps_each_folder_onto_a_child_of_the_mount_when_that_child_holds_the_tracks()
    {
        var locations = new[] { "/mnt/user/media/music", "/mnt/user/media/classical" };
        var samples = new[]
        {
            "/mnt/user/media/music/A/01.flac",
            "/mnt/user/media/classical/B/02.flac"
        };
        var ok = LyricsLibraryMapper.TryMatch(
            locations,
            "/music",
            samples,
            path => path is "/music/music" or "/music/classical",
            path => path is "/music/music/A/01.flac" or "/music/classical/B/02.flac",
            out var matches,
            out var maps,
            out var error);

        Assert.True(ok, error);
        Assert.Equal(2, matches.Count);
        Assert.All(matches, match => Assert.Equal(LyricsMapStatus.Ok, match.Status));
        Assert.Equal("/music/music", matches[0].ContainerPrefix);
        Assert.Equal("/music/classical", matches[1].ContainerPrefix);
        Assert.Equal("/mnt/user/media/music", maps[0].PlexPrefix);
        Assert.Equal("/music/music", maps[0].ContainerPrefix);
        Assert.Equal("/mnt/user/media/classical", maps[1].PlexPrefix);
        Assert.Equal("/music/classical", maps[1].ContainerPrefix);
    }

    [Fact]
    public void Leaves_a_folder_not_found_when_its_tracks_are_not_on_the_mount()
    {
        var ok = LyricsLibraryMapper.TryMatch(
            ["/mnt/user/media/music", "/mnt/user/media/classical"],
            "/music",
            ["/mnt/user/media/music/A/01.flac", "/mnt/user/media/classical/B/02.flac"],
            path => path == "/music/music",
            path => path == "/music/music/A/01.flac",
            out var matches,
            out var maps,
            out var error);

        Assert.True(ok, error);
        Assert.Equal(LyricsMapStatus.Ok, matches[0].Status);
        Assert.Equal(LyricsMapStatus.NotFound, matches[1].Status);
        Assert.Null(matches[1].ContainerPrefix);
        Assert.Equal("/mnt/user/media/music", Assert.Single(maps).PlexPrefix);
    }

    [Fact]
    public void Uses_the_mount_itself_when_the_child_folder_exists_but_the_tracks_are_not_in_it()
    {
        var ok = LyricsLibraryMapper.TryMatch(
            ["/mnt/user/media/music"],
            "/music",
            ["/mnt/user/media/music/A/01.flac"],
            path => path == "/music/music",
            path => path == "/music/A/01.flac",
            out var matches,
            out var maps,
            out var error);

        Assert.True(ok, error);
        Assert.Equal("/music", Assert.Single(matches).ContainerPrefix);
        Assert.Equal("/music", Assert.Single(maps).ContainerPrefix);
    }

    [Fact]
    public void Prefers_the_child_folder_when_the_same_tracks_exist_in_both_places()
    {
        var ok = LyricsLibraryMapper.TryMatch(
            ["/mnt/user/media/music"],
            " /music/ ",
            ["/mnt/user/media/music/A/01.flac"],
            _ => true,
            path => path is "/music/music/A/01.flac" or "/music/A/01.flac",
            out var matches,
            out _,
            out var error);

        Assert.True(ok, error);
        Assert.Equal("/music/music", Assert.Single(matches).ContainerPrefix);
        Assert.Equal(LyricsMapStatus.Ok, matches[0].Status);
    }

    [Fact]
    public void Maps_a_windows_plex_folder_onto_the_container_mount()
    {
        var ok = LyricsLibraryMapper.TryMatch(
            [@"D:\Music"],
            null,
            [@"D:\Music\Artist\Album\song.flac"],
            _ => false,
            path => path == "/music/Artist/Album/song.flac",
            out var matches,
            out var maps,
            out var error);

        Assert.True(ok, error);
        Assert.Equal(LyricsMapStatus.Ok, Assert.Single(matches).Status);
        Assert.Equal("/music", maps[0].ContainerPrefix);
        Assert.Equal(@"D:\Music", maps[0].PlexPrefix);
    }

    [Fact]
    public void Does_not_treat_a_longer_name_as_the_same_folder()
    {
        var ok = LyricsLibraryMapper.TryMatch(
            ["/data/music"],
            "/music",
            ["/data/music2/song.flac", "/data/musical/song.flac"],
            _ => false,
            _ => true,
            out var matches,
            out var maps,
            out var error);

        Assert.True(ok, error);
        Assert.Equal(LyricsMapStatus.NotFound, Assert.Single(matches).Status);
        Assert.Empty(maps);
        Assert.Equal(0, matches[0].SamplesChecked);
    }

    [Fact]
    public void Gives_a_track_to_the_longer_library_folder()
    {
        var ok = LyricsLibraryMapper.TryMatch(
            ["/data/music", "/data/music/classical"],
            "/media",
            ["/data/music/classical/Bach/01.flac"],
            path => path == "/media/classical",
            path => path == "/media/classical/Bach/01.flac",
            out var matches,
            out var maps,
            out var error);

        Assert.True(ok, error);
        Assert.Equal(LyricsMapStatus.NotFound, matches[0].Status);
        Assert.Equal(LyricsMapStatus.Ok, matches[1].Status);
        Assert.Equal("/media/classical", matches[1].ContainerPrefix);
        Assert.Equal("/data/music/classical", Assert.Single(maps).PlexPrefix);
    }

    [Fact]
    public void Checks_only_a_handful_of_tracks_per_folder()
    {
        var samples = Enumerable.Range(1, 6).Select(index => "/mnt/user/media/music/A/" + index + ".flac").ToList();
        var ok = LyricsLibraryMapper.TryMatch(
            ["/mnt/user/media/music"],
            "/music",
            samples,
            _ => false,
            path => path.EndsWith("/6.flac", StringComparison.Ordinal),
            out var matches,
            out var maps,
            out var error);

        Assert.True(ok, error);
        Assert.Equal(LyricsMapStatus.NotFound, Assert.Single(matches).Status);
        Assert.Equal(5, matches[0].SamplesChecked);
        Assert.Equal(0, matches[0].SamplesFound);
        Assert.Empty(maps);

        ok = LyricsLibraryMapper.TryMatch(
            ["/mnt/user/media/music"],
            "/music",
            samples,
            _ => false,
            path => path.EndsWith("/3.flac", StringComparison.Ordinal),
            out matches,
            out maps,
            out error);
        Assert.True(ok, error);
        Assert.Equal(LyricsMapStatus.Ok, matches[0].Status);
        Assert.Equal(1, matches[0].SamplesFound);
        Assert.Equal(5, matches[0].SamplesChecked);
    }

    [Fact]
    public void Rejects_a_container_folder_that_is_not_a_music_mount()
    {
        var ok = LyricsLibraryMapper.TryMatch(["/data/music"], "/config", ["/data/music/a.flac"], _ => false, _ => true, out _, out _, out var error);
        Assert.False(ok);
        Assert.False(string.IsNullOrWhiteSpace(error));
        Assert.DoesNotContain("token", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Round_trips_a_check_without_a_token()
    {
        var matches = new[] { new LyricsLocationMatch("/mnt/user/media/music", "/music", LyricsMapStatus.Ok, 2, 2) };
        var again = LyricsLibraryMapper.MatchesFromJson(LyricsLibraryMapper.MatchesToJson(matches));
        Assert.Equal(matches, again);
        Assert.DoesNotContain("token", LyricsLibraryMapper.MatchesToJson(matches), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Stops_sampling_once_each_folder_has_a_handful()
    {
        var locations = new[] { "/data/music", "/data/classical" };
        var few = new[] { "/data/music/a.flac", "/data/classical/b.flac" };
        Assert.False(LyricsLibraryMapper.HasEnoughSamples(locations, few));
        var enough = Enumerable.Range(1, 5).SelectMany(index => new[]
        {
            "/data/music/" + index + ".flac",
            "/data/classical/" + index + ".flac"
        }).ToList();
        Assert.True(LyricsLibraryMapper.HasEnoughSamples(locations, enough));
        Assert.False(LyricsLibraryMapper.HasEnoughSamples(locations, enough.Where(path => path.Contains("/classical/", StringComparison.Ordinal)).ToList()));
    }
}

public class LyricsLibrarySaveTests
{
    private const string Token = "library-map-token-do-not-leak";

    [Fact]
    public async Task Checking_a_library_maps_each_location_and_does_not_store_the_token()
    {
        using var fixture = new ServiceFixture();
        var root = Path.Combine(fixture.Root, "mount");
        Directory.CreateDirectory(Path.Combine(root, "music", "A"));
        Directory.CreateDirectory(Path.Combine(root, "classical", "B"));
        File.WriteAllBytes(Path.Combine(root, "music", "A", "01.flac"), [1]);
        File.WriteAllBytes(Path.Combine(root, "classical", "B", "02.flac"), [1]);
        fixture.Handler.Responder = request =>
        {
            if (request.Uri.AbsolutePath == "/library/sections")
                return HttpJson.Json(Sections("""[{"path":"/mnt/user/media/music"},{"path":"/mnt/user/media/classical"}]"""));
            if (request.Uri.AbsolutePath == "/library/sections/5/all")
                return HttpJson.Json(Page(
                    Track("11", "/mnt/user/media/music/A/01.flac"),
                    Track("12", "/mnt/user/media/classical/B/02.flac")));
            return HttpJson.Json("{}", System.Net.HttpStatusCode.NotFound);
        };

        var saved = await fixture.Get<PlexServerService>().SaveAsync(null, "Shelf", "http://plex.test:32400", Token);
        var page = PageService(fixture);
        var report = await page.MapLibraryAsync(saved.Id, "5", root);

        Assert.Equal("Music", report.SectionTitle);
        Assert.Equal(root, report.ContainerFolder);
        Assert.Equal(2, report.Maps.Count);
        Assert.All(report.Matches, match => Assert.Equal(LyricsMapStatus.Ok, match.Status));
        Assert.Contains(report.Maps, map => map.PlexPrefix == "/mnt/user/media/music" && map.ContainerPrefix == Path.Combine(root, "music"));
        Assert.Contains(report.Maps, map => map.PlexPrefix == "/mnt/user/media/classical" && map.ContainerPrefix == Path.Combine(root, "classical"));
        Assert.Empty(await Settings(fixture));
        Assert.All(fixture.Handler.Requests, request => Assert.DoesNotContain("X-Plex-Token", request.Uri.Query, StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(fixture.Logs.Logger.Messages, message => message.Contains(Token, StringComparison.Ordinal));
        fixture.AssertNoSecret(Token);
    }

    [Fact]
    public async Task Saving_a_library_keeps_existing_prefixes_when_no_file_is_found()
    {
        using var fixture = new ServiceFixture();
        fixture.Handler.Responder = request =>
        {
            if (request.Uri.AbsolutePath == "/library/sections")
                return HttpJson.Json(Sections("""[{"path":"/mnt/user/media/music"}]"""));
            if (request.Uri.AbsolutePath == "/library/sections/5/all")
                return HttpJson.Json(Page(Track("11", "/mnt/user/media/music/A/01.flac")));
            return HttpJson.Json("{}", System.Net.HttpStatusCode.NotFound);
        };
        var saved = await fixture.Get<PlexServerService>().SaveAsync(null, "Shelf", "http://plex.test:32400", Token);
        var page = PageService(fixture);
        await page.SaveAsync(new LyricsSettingsInput
        {
            ServerId = saved.Id,
            AutoCheck = true,
            IntervalHours = 12,
            Maps = [new LyricsMapInput { PlexPrefix = "/mnt/user/media/music", ContainerPrefix = "/music" }]
        });
        var before = Assert.Single(await Settings(fixture));
        var stamp = before.LastAutoCheckUtc;

        var report = await page.SaveLibraryAsync(new LyricsSettingsInput
        {
            ServerId = saved.Id,
            AutoCheck = true,
            IntervalHours = 6,
            SectionKey = "5",
            ContainerFolder = "/music"
        });

        Assert.Empty(report.Maps);
        Assert.Equal(LyricsMapStatus.NotFound, Assert.Single(report.Matches).Status);
        var row = Assert.Single(await Settings(fixture));
        Assert.Equal(before.PathMapsJson, row.PathMapsJson);
        Assert.Contains("/mnt/user/media/music", row.PathMapsJson);
        Assert.Equal("5", row.MusicSectionKey);
        Assert.Equal("/music", row.ContainerFolder);
        Assert.False(row.UseManualMaps);
        Assert.Contains("Not found", row.LocationMatchesJson);
        Assert.DoesNotContain(Token, row.LocationMatchesJson, StringComparison.Ordinal);
        Assert.Equal(stamp, row.LastAutoCheckUtc);
        Assert.Equal(6, row.AutoCheckIntervalHours);
        await AssertTokenAndSlot(fixture, saved.Id, Token, 1);
    }

    [Fact]
    public async Task A_manual_prefix_replaces_the_library_mapping_and_leaves_the_token()
    {
        using var fixture = new ServiceFixture();
        var saved = await fixture.Get<PlexServerService>().SaveAsync(null, "Shelf", "http://plex.test:32400", Token);
        var page = PageService(fixture);
        await page.SaveAsync(new LyricsSettingsInput
        {
            ServerId = saved.Id,
            SectionKey = "5",
            ContainerFolder = "/music",
            UseManualMaps = true,
            Maps = [new LyricsMapInput { PlexPrefix = @"D:\Music", ContainerPrefix = "/music" }],
            Matches = [new LyricsLocationMatch("/old", "/music", LyricsMapStatus.Ok, 1, 1)]
        });

        var row = Assert.Single(await Settings(fixture));
        Assert.True(row.UseManualMaps);
        Assert.Contains(@"D:\\Music", row.PathMapsJson);
        Assert.Null(row.LocationMatchesJson);
        Assert.Equal("5", row.MusicSectionKey);
        Assert.Empty(fixture.Handler.Requests);
        await AssertTokenAndSlot(fixture, saved.Id, Token, 1);
    }

    [Fact]
    public async Task A_movie_library_and_a_blocked_folder_are_refused_before_any_map_is_stored()
    {
        using var fixture = new ServiceFixture();
        fixture.Handler.Responder = request => request.Uri.AbsolutePath == "/library/sections"
            ? HttpJson.Json(Sections("""[{"path":"/mnt/user/media/music"}]"""))
            : HttpJson.Json("{}", System.Net.HttpStatusCode.NotFound);
        var saved = await fixture.Get<PlexServerService>().SaveAsync(null, "Shelf", "http://plex.test:32400", Token);
        var page = PageService(fixture);
        var movie = await Assert.ThrowsAsync<PlexBridgeException>(() => page.MapLibraryAsync(saved.Id, "1", "/music"));
        Assert.Contains("music library", movie.Message, StringComparison.OrdinalIgnoreCase);
        fixture.Handler.Requests.Clear();
        var blocked = await Assert.ThrowsAsync<PlexBridgeException>(() => page.MapLibraryAsync(saved.Id, "5", "/config"));
        Assert.False(string.IsNullOrWhiteSpace(blocked.Message));
        Assert.DoesNotContain(Token, blocked.Message, StringComparison.Ordinal);
        Assert.Empty(fixture.Handler.Requests);
        Assert.Empty(await Settings(fixture));
        fixture.AssertNoSecret(Token);
    }

    [Fact]
    public async Task Reads_another_page_when_the_first_page_misses_a_folder()
    {
        using var fixture = new ServiceFixture();
        var root = Path.Combine(fixture.Root, "mount");
        Directory.CreateDirectory(Path.Combine(root, "music"));
        Directory.CreateDirectory(Path.Combine(root, "classical"));
        File.WriteAllBytes(Path.Combine(root, "music", "01.flac"), [1]);
        File.WriteAllBytes(Path.Combine(root, "classical", "02.flac"), [1]);
        fixture.Handler.Responder = request =>
        {
            if (request.Uri.AbsolutePath == "/library/sections")
                return HttpJson.Json(Sections("""[{"path":"/mnt/user/media/music"},{"path":"/mnt/user/media/classical"}]"""));
            if (request.Uri.Query.Contains("X-Plex-Container-Start=0", StringComparison.Ordinal))
                return HttpJson.Json(Page(Track("11", "/mnt/user/media/music/01.flac"), Track("12", "/mnt/user/media/music/ignored.flac")));
            if (request.Uri.Query.Contains("X-Plex-Container-Start=2", StringComparison.Ordinal))
                return HttpJson.Json(Page(Track("13", "/mnt/user/media/classical/02.flac")));
            return HttpJson.Json(Page());
        };
        var saved = await fixture.Get<PlexServerService>().SaveAsync(null, "Shelf", "http://plex.test:32400", Token);
        var lyrics = Client(fixture);
        lyrics.TrackPageSize = 2;
        var page = PageService(fixture, lyrics);
        var report = await page.MapLibraryAsync(saved.Id, "5", root);

        Assert.Equal(2, report.Maps.Count);
        Assert.Equal(2, fixture.Handler.Requests.Count(request => request.Uri.AbsolutePath == "/library/sections/5/all"));
        Assert.All(fixture.Handler.Requests, request => Assert.DoesNotContain(Token, request.Uri.Query, StringComparison.Ordinal));
        fixture.AssertNoSecret(Token);
    }

    [Fact]
    public async Task Adding_library_columns_keeps_saved_prefixes_tokens_slots_and_keys()
    {
        var root = Path.Combine(Path.GetTempPath(), "plexbridge-unit", Guid.NewGuid().ToString("n"));
        var keys = Path.Combine(root, "keys");
        Directory.CreateDirectory(keys);
        var databasePath = Path.Combine(root, "plexbridge.db");
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo(keys)).SetApplicationName("PlexBridgeTests");
        services.AddSingleton<ISecretProtector, DataProtectionSecretProtector>();
        services.AddDbContext<ApplicationDbContext>(options => options.UseSqlite("Data Source=" + databasePath));
        await using var provider = services.BuildServiceProvider();
        int serverId;
        string protectedToken;
        Dictionary<string, byte[]> keyBytes = [];
        var maps = LyricsPaths.ToJson([new LyricsPathMap("/mnt/user/media/music", "/music")]);
        var stamp = new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);
        await using (var scope = provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var migrator = db.Database.GetService<IMigrator>() ?? throw new InvalidOperationException("Missing migrator.");
            await migrator.MigrateAsync("20261116022343_Lyrics");
            var protector = scope.ServiceProvider.GetRequiredService<ISecretProtector>();
            protectedToken = protector.Protect(Token);
            db.PlexServers.Add(new PlexServerConnection
            {
                Name = "Shelf",
                BaseUrl = "http://192.0.2.20:32400",
                ProtectedToken = protectedToken,
                Slot = 2,
                Status = "Online",
                UpdatedUtc = DateTimeOffset.UtcNow
            });
            await db.SaveChangesAsync();
            serverId = db.PlexServers.Single().Id;
            keyBytes = Directory.GetFiles(keys).ToDictionary(path => path, File.ReadAllBytes);
            Assert.NotEmpty(keyBytes);
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO LyricsSettings (PlexServerConnectionId, PathMapsJson, AutoCheckEnabled, AutoCheckIntervalHours, LastAutoCheckUtc)
                VALUES ({serverId}, {maps}, {true}, {12}, {stamp})
                """);
            await migrator.MigrateAsync();
        }

        await using (var scope = provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var row = await db.LyricsSettings.AsNoTracking().SingleAsync();
            var server = await db.PlexServers.AsNoTracking().SingleAsync();
            Assert.Equal(maps, row.PathMapsJson);
            Assert.Null(row.MusicSectionKey);
            Assert.Null(row.ContainerFolder);
            Assert.False(row.UseManualMaps);
            Assert.Null(row.LocationMatchesJson);
            Assert.True(row.AutoCheckEnabled);
            Assert.Equal(12, row.AutoCheckIntervalHours);
            Assert.Equal(stamp, row.LastAutoCheckUtc);
            Assert.Equal(2, server.Slot);
            Assert.Equal("http://192.0.2.20:32400", server.BaseUrl);
            Assert.Equal(protectedToken, server.ProtectedToken);
            Assert.Equal(Token, scope.ServiceProvider.GetRequiredService<ISecretProtector>().Unprotect(server.ProtectedToken!));
            Assert.Equal(keyBytes.Keys.OrderBy(path => path), Directory.GetFiles(keys).OrderBy(path => path));
            foreach (var (path, bytes) in keyBytes)
                Assert.Equal(bytes, File.ReadAllBytes(path));
            foreach (var file in Directory.GetFiles(root, "*", SearchOption.AllDirectories))
            {
                var stored = Encoding.UTF8.GetString(await File.ReadAllBytesAsync(file));
                Assert.DoesNotContain(Token, stored, StringComparison.Ordinal);
            }
        }

        try { Directory.Delete(root, true); } catch { /* the assertions already read the files */ }
    }

    private static LyricsPageService PageService(ServiceFixture fixture, PlexLyricsClient? lyrics = null)
        => new(
            fixture.Provider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>(),
            fixture.Provider.GetRequiredService<ISecretProtector>(),
            fixture.Get<PlexReadClient>(),
            lyrics ?? Client(fixture),
            new StubAdmin("admin"),
            new LyricsJobs(fixture.Provider.GetRequiredService<IServiceScopeFactory>(), NullLogger<LyricsJobs>.Instance),
            fixture.SyncQueue(),
            fixture.SyncRefresh(lyrics));

    private static PlexLyricsClient Client(ServiceFixture fixture)
    {
        var transport = new HandlerTransport(fixture.Handler);
        return new PlexLyricsClient(transport, new ForwardingPlexWriteTransport(transport), new AllowAllAddressGuard(), NullLogger<PlexLyricsClient>.Instance);
    }

    private static async Task<List<LyricsSetting>> Settings(ServiceFixture fixture)
    {
        await using var scope = fixture.Provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return await db.LyricsSettings.AsNoTracking().ToListAsync();
    }

    private static async Task AssertTokenAndSlot(ServiceFixture fixture, int serverId, string token, int slot)
    {
        await using var scope = fixture.Provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var server = await db.PlexServers.AsNoTracking().SingleAsync(item => item.Id == serverId);
        Assert.Equal(slot, server.Slot);
        Assert.Equal(token, scope.ServiceProvider.GetRequiredService<ISecretProtector>().Unprotect(server.ProtectedToken!));
        Assert.NotEqual(token, server.ProtectedToken);
        fixture.AssertNoSecret(token);
    }

    private static string Sections(string locations)
        => "{\"MediaContainer\":{\"Directory\":[{\"key\":\"1\",\"type\":\"movie\",\"title\":\"Films\",\"Location\":[{\"path\":\"/demo/films\"}]},{\"key\":\"5\",\"type\":\"artist\",\"title\":\"Music\",\"Location\":" + locations + "}]}}";

    private static string Page(params string[] items)
        => "{\"MediaContainer\":{\"size\":" + items.Length + ",\"totalSize\":10,\"Metadata\":[" + string.Join(",", items) + "]}}";

    private static string Track(string key, string file)
        => "{\"ratingKey\":\"" + key + "\",\"type\":\"track\",\"title\":\"Song\",\"grandparentTitle\":\"Artist\",\"parentTitle\":\"Album\",\"parentRatingKey\":\"2\",\"grandparentRatingKey\":\"1\",\"duration\":1000,\"Media\":[{\"Part\":[{\"file\":\"" + file.Replace("\\", "\\\\") + "\"}]}]}";

    private sealed class StubAdmin(string id) : IAdminGate
    {
        public Task<string> RequireUserIdAsync(CancellationToken cancellationToken) => Task.FromResult(id);
    }
}
