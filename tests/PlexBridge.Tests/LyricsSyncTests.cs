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

public class LyricsSyncTests
{
    private const string Token = "sync-refresh-token-do-not-leak";

    [Fact]
    public async Task Due_tracks_on_one_album_refresh_once_and_a_lyrics_stream_finishes_the_wait()
    {
        using var fixture = new ServiceFixture();
        var clock = new MutableClock(new DateTimeOffset(2026, 11, 16, 12, 0, 0, TimeSpan.Zero));
        var serverId = await SaveServer(fixture);
        await SeedSettings(fixture, serverId, 2, 30);
        var queue = new LyricsSyncQueue(fixture.Get<IDbContextFactory<ApplicationDbContext>>(), clock);
        await queue.RecordAsync(serverId, [
            Written("10", "55", showsLyrics: false),
            Written("11", "55", showsLyrics: false),
            Written("12", "55", showsLyrics: true)
        ], CancellationToken.None);

        var before = await OpenItems(fixture);
        Assert.Equal(2, before.Count);
        var listed = await queue.ListOpenAsync(CancellationToken.None);
        Assert.Equal(new[] { "10", "11" }, listed.Select(item => item.RatingKey).OrderBy(key => key));
        Assert.DoesNotContain(before, item => item.RatingKey == "12");
        Assert.All(before, item => Assert.Equal(LyricsSyncStatus.Waiting, item.Status));

        clock.Now = clock.Now.AddMinutes(2);
        var refreshes = 0;
        fixture.Handler.Responder = request =>
        {
            Assert.Equal(Token, request.Token);
            Assert.DoesNotContain("X-Plex-Token", request.Uri.Query, StringComparison.OrdinalIgnoreCase);
            if (request.Method == HttpMethod.Put)
            {
                refreshes++;
                Assert.Equal("/library/metadata/55/refresh", request.Uri.AbsolutePath);
                return HttpJson.Json("""{"MediaContainer":{}}""");
            }

            var lyrics = request.Uri.AbsolutePath.EndsWith("/10", StringComparison.Ordinal);
            return HttpJson.Json(Track(lyrics));
        };

        var sidecar = Path.Combine(fixture.Root, "song.lrc");
        await File.WriteAllTextAsync(sidecar, "keep");
        var service = Service(fixture, clock);
        await service.RefreshDueAsync(CancellationToken.None);
        await service.RefreshDueAsync(CancellationToken.None);

        Assert.Equal(1, refreshes);
        Assert.Equal("keep", await File.ReadAllTextAsync(sidecar));
        var waiting = Assert.Single(await OpenItems(fixture));
        Assert.Equal("11", waiting.RatingKey);
        Assert.Equal(LyricsSyncStatus.Waiting, waiting.Status);
        Assert.True(waiting.NextRefreshUtc > clock.Now);
        await using var scope = fixture.Provider.CreateAsyncScope();
        var done = await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().LyricsSyncItems.SingleAsync(item => item.RatingKey == "10");
        Assert.Equal(LyricsSyncStatus.Done, done.Status);
        fixture.AssertNoSecret(Token);
    }

    [Fact]
    public async Task A_track_that_never_shows_a_lyrics_stream_is_marked_missed()
    {
        using var fixture = new ServiceFixture();
        var clock = new MutableClock(new DateTimeOffset(2026, 11, 16, 12, 0, 0, TimeSpan.Zero));
        var serverId = await SaveServer(fixture);
        await SeedSettings(fixture, serverId, 2, 30);
        var queue = new LyricsSyncQueue(fixture.Get<IDbContextFactory<ApplicationDbContext>>(), clock);
        await queue.RecordAsync(serverId, [Written("10", "55", showsLyrics: false), Written("20", "77", showsLyrics: false)], CancellationToken.None);
        clock.Now = clock.Now.AddMinutes(31);
        var albums = new List<string>();
        fixture.Handler.Responder = request =>
        {
            if (request.Method == HttpMethod.Put)
            {
                albums.Add(request.Uri.AbsolutePath);
                return HttpJson.Json("""{"MediaContainer":{}}""");
            }

            return HttpJson.Json(Track(false));
        };

        await Service(fixture, clock).RefreshDueAsync(CancellationToken.None);
        var rows = await AllItems(fixture);
        Assert.Equal(2, rows.Count);
        Assert.All(rows, row => Assert.Equal(LyricsSyncStatus.Missed, row.Status));
        Assert.Contains("/library/metadata/55/refresh", albums);
        Assert.Contains("/library/metadata/77/refresh", albums);
        Assert.Equal(2, albums.Count);

        fixture.Handler.Requests.Clear();
        await Service(fixture, clock).RefreshDueAsync(CancellationToken.None);
        Assert.DoesNotContain(fixture.Handler.Requests, request => request.Method == HttpMethod.Put);
    }

    [Fact]
    public async Task A_manual_refresh_finishes_a_missed_track_when_plex_has_the_stream()
    {
        using var fixture = new ServiceFixture();
        var clock = new MutableClock(new DateTimeOffset(2026, 11, 16, 12, 0, 0, TimeSpan.Zero));
        var serverId = await SaveServer(fixture);
        var queue = new LyricsSyncQueue(fixture.Get<IDbContextFactory<ApplicationDbContext>>(), clock);
        await queue.RecordAsync(serverId, [Written("10", "55", showsLyrics: false)], CancellationToken.None);
        clock.Now = clock.Now.AddMinutes(31);
        fixture.Handler.Responder = request => request.Method == HttpMethod.Put
            ? HttpJson.Json("""{"MediaContainer":{}}""")
            : HttpJson.Json(Track(false));
        var service = Service(fixture, clock);
        await service.RefreshDueAsync(CancellationToken.None);
        var missed = Assert.Single(await OpenItems(fixture));
        Assert.Equal(LyricsSyncStatus.Missed, missed.Status);

        fixture.Handler.Responder = request => request.Method == HttpMethod.Put
            ? HttpJson.Json("""{"MediaContainer":{}}""")
            : HttpJson.Json(Track(true));
        var message = await service.RefreshNowAsync(missed.Id, CancellationToken.None);
        Assert.Equal("Plex shows a lyrics stream.", message);
        Assert.Empty(await OpenItems(fixture));
        Assert.Contains(fixture.Handler.Requests, request => request.Method == HttpMethod.Put && request.Uri.AbsolutePath == "/library/metadata/55/refresh");
        fixture.AssertNoSecret(Token);
    }

    [Fact]
    public async Task Adding_the_sync_table_keeps_saved_prefixes_tokens_and_slots()
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
        string protectedToken;
        Dictionary<string, byte[]> keyBytes;
        var maps = LyricsPaths.ToJson([new LyricsPathMap(@"M:\Music", "/libraries/music")]);
        int serverId;
        await using (var scope = provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var migrator = db.Database.GetService<IMigrator>() ?? throw new InvalidOperationException("Missing migrator.");
            await migrator.MigrateAsync("20261116034736_LibraryMount");
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
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO LyricsSettings (PlexServerConnectionId, PathMapsJson, AutoCheckEnabled, AutoCheckIntervalHours, LastAutoCheckUtc, ContainerFolder, UseManualMaps, MusicSectionKey, MusicSectionTitle, LocationMatchesJson)
                VALUES ({serverId}, {maps}, {false}, {12}, {null}, {"/libraries/music"}, {true}, {"5"}, {"Music"}, {null})
                """);
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO LibraryMounts (PlexServerConnectionId, ContainerFolder, UseManualMaps, PathMapsJson, SectionMatchesJson)
                VALUES ({serverId}, {"/libraries/music"}, {true}, {maps}, {null})
                """);
            await migrator.MigrateAsync();
        }

        await using (var scope = provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var row = await db.LyricsSettings.AsNoTracking().SingleAsync();
            var mount = await db.LibraryMounts.AsNoTracking().SingleAsync();
            var server = await db.PlexServers.AsNoTracking().SingleAsync();
            Assert.Equal(maps, row.PathMapsJson);
            Assert.Equal(maps, mount.PathMapsJson);
            Assert.True(row.UseManualMaps);
            Assert.True(mount.UseManualMaps);
            Assert.Equal("/libraries/music", row.ContainerFolder);
            Assert.Equal("5", row.MusicSectionKey);
            Assert.Equal(2, row.SyncIntervalMinutes);
            Assert.Equal(30, row.SyncWindowMinutes);
            Assert.Equal(2, server.Slot);
            Assert.Equal(protectedToken, server.ProtectedToken);
            Assert.Equal(Token, scope.ServiceProvider.GetRequiredService<ISecretProtector>().Unprotect(server.ProtectedToken!));
            Assert.Empty(await db.LyricsSyncItems.ToListAsync());
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

    [Fact]
    public async Task A_pending_row_is_still_there_for_a_new_scheduler()
    {
        using var fixture = new ServiceFixture();
        var clock = new MutableClock(new DateTimeOffset(2026, 11, 16, 12, 0, 0, TimeSpan.Zero));
        var serverId = await SaveServer(fixture);
        var queue = new LyricsSyncQueue(fixture.Get<IDbContextFactory<ApplicationDbContext>>(), clock);
        await queue.RecordAsync(serverId, [Written("10", "", showsLyrics: false)], CancellationToken.None);
        clock.Now = clock.Now.AddMinutes(2);
        fixture.Handler.Responder = request =>
        {
            if (request.Method == HttpMethod.Put)
            {
                Assert.Equal("/library/metadata/10/refresh", request.Uri.AbsolutePath);
                return HttpJson.Json("""{"MediaContainer":{}}""");
            }

            return HttpJson.Json(Track(true));
        };

        var restarted = Service(fixture, clock);
        await restarted.RefreshDueAsync(CancellationToken.None);
        Assert.Empty(await OpenItems(fixture));
        await using var scope = fixture.Provider.CreateAsyncScope();
        var row = await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().LyricsSyncItems.SingleAsync();
        Assert.Equal(LyricsSyncStatus.Done, row.Status);
    }

    private static LyricsSyncRefreshService Service(ServiceFixture fixture, TimeProvider clock)
    {
        var transport = new HandlerTransport(fixture.Handler);
        var plex = new PlexLyricsClient(transport, new ForwardingPlexWriteTransport(transport), new AllowAllAddressGuard(), NullLogger<PlexLyricsClient>.Instance);
        return new LyricsSyncRefreshService(
            fixture.Get<IDbContextFactory<ApplicationDbContext>>(),
            fixture.Get<ISecretProtector>(),
            plex,
            clock,
            NullLogger<LyricsSyncRefreshService>.Instance);
    }

    private static async Task<int> SaveServer(ServiceFixture fixture)
    {
        var saved = await fixture.Get<PlexServerService>().SaveAsync(null, "Shelf", "http://plex.test:32400", Token);
        return saved.Id;
    }

    private static async Task SeedSettings(ServiceFixture fixture, int serverId, int interval, int window)
    {
        await using var scope = fixture.Provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        db.LyricsSettings.Add(new LyricsSetting
        {
            PlexServerConnectionId = serverId,
            PathMapsJson = "[]",
            SyncIntervalMinutes = interval,
            SyncWindowMinutes = window
        });
        await db.SaveChangesAsync();
    }

    private static async Task<List<LyricsSyncItem>> OpenItems(ServiceFixture fixture)
    {
        var rows = await AllItems(fixture);
        return rows.Where(row => row.Status is LyricsSyncStatus.Waiting or LyricsSyncStatus.Missed).ToList();
    }

    private static async Task<List<LyricsSyncItem>> AllItems(ServiceFixture fixture)
    {
        await using var scope = fixture.Provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return await db.LyricsSyncItems.AsNoTracking().OrderBy(item => item.RatingKey).ToListAsync();
    }

    private static LyricsTrackRow Written(string ratingKey, string albumKey, bool showsLyrics)
        => new()
        {
            RatingKey = ratingKey,
            AlbumKey = albumKey,
            Artist = "Artist",
            Album = "Album",
            Title = "Song " + ratingKey,
            FileName = "song.flac",
            ApplyResult = "Wrote song.lrc.",
            PlexShowsLyrics = showsLyrics
        };

    private static string Track(bool lyrics)
    {
        var stream = lyrics
            ? ",{\"id\":9,\"streamType\":4,\"codec\":\"lrc\",\"provider\":\"com.plexapp.agents.localmedia\"}"
            : "";
        return "{\"MediaContainer\":{\"size\":1,\"Metadata\":[{\"ratingKey\":\"10\",\"type\":\"track\",\"title\":\"Song\",\"Media\":[{\"Part\":[{\"file\":\"M:\\\\Music\\\\song.flac\",\"Stream\":[{\"id\":1,\"streamType\":2,\"codec\":\"flac\"}" + stream + "]}]}]}]}}";
    }

    private sealed class MutableClock(DateTimeOffset start) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = start;
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
