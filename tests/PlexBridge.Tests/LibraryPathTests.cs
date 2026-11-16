using System.Net;
using System.Text;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using PlexBridge.Data;
using PlexBridge.Libraries;
using PlexBridge.Lyrics;
using PlexBridge.Plex;
using PlexBridge.Security;
using PlexBridge.Services;

namespace PlexBridge.Tests;

public class LibraryPathMapperTests
{
    [Fact]
    public void Maps_a_media_suffix_onto_the_libraries_mount()
    {
        var ok = LibraryPathMapper.TryMatch(
            ["/mnt/user/media/music"],
            "/libraries",
            ["/mnt/user/media/music/Artist/Album/01.flac"],
            path => path == "/libraries/music",
            path => path == "/libraries/music/Artist/Album/01.flac",
            out var matches,
            out var maps,
            out var error);

        Assert.True(ok, error);
        Assert.Equal(LibraryMapStatus.Ok, Assert.Single(matches).Status);
        Assert.Equal("/libraries/music", Assert.Single(matches).ContainerPrefix);
        Assert.Equal("/mnt/user/media/music", Assert.Single(maps).PlexPrefix);
        Assert.Equal("/libraries/music", Assert.Single(maps).ContainerPrefix);
    }

    [Fact]
    public void Prefers_the_longest_trailing_suffix_that_holds_the_files()
    {
        var ok = LibraryPathMapper.TryMatch(
            ["/data/media/music"],
            "/libraries",
            ["/data/media/music/A/01.flac"],
            path => path is "/libraries/media/music" or "/libraries/music",
            path => path is "/libraries/media/music/A/01.flac" or "/libraries/music/A/01.flac",
            out var matches,
            out var maps,
            out var error);

        Assert.True(ok, error);
        Assert.Equal("/libraries/media/music", Assert.Single(matches).ContainerPrefix);
        Assert.Equal("/libraries/media/music", Assert.Single(maps).ContainerPrefix);
    }

    [Fact]
    public void Maps_a_windows_drive_onto_a_case_insensitive_folder()
    {
        var ok = LibraryPathMapper.TryMatch(
            [@"M:\Music"],
            "/libraries",
            [@"M:\Music\Artist\Album\song.flac"],
            path => path == "/libraries/music",
            path => path == "/libraries/music/Artist/Album/song.flac",
            out var matches,
            out var maps,
            out var error,
            path => path == "/libraries" ? ["/libraries/music"] : []);

        Assert.True(ok, error);
        var match = Assert.Single(matches);
        Assert.Equal(LibraryMapStatus.Ok, match.Status);
        Assert.Equal("/libraries/music", match.ContainerPrefix);
        Assert.Equal(@"M:\Music", Assert.Single(maps).PlexPrefix);
        Assert.Equal("/libraries/music", Assert.Single(maps).ContainerPrefix);
        Assert.DoesNotContain("token", LibraryPathMapper.MapsToJson(maps), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Maps_a_windows_path_with_more_than_one_folder()
    {
        var ok = LibraryPathMapper.TryMatch(
            [@"D:\Media\Music"],
            "/libraries",
            [@"D:\Media\Music\Artist\song.flac"],
            path => path == "/libraries/Media/Music",
            path => path == "/libraries/Media/Music/Artist/song.flac",
            out var matches,
            out var maps,
            out var error);

        Assert.True(ok, error);
        Assert.Equal("/libraries/Media/Music", Assert.Single(matches).ContainerPrefix);
        Assert.Equal(@"D:\Media\Music", Assert.Single(maps).PlexPrefix);
    }

    [Fact]
    public void Keeps_a_library_that_is_mounted_directly_at_music()
    {
        var ok = LibraryPathMapper.TryMatch(
            ["/mnt/user/media/music"],
            "/music",
            ["/mnt/user/media/music/Artist/Album/01.flac"],
            _ => false,
            path => path == "/music/Artist/Album/01.flac",
            out var matches,
            out var maps,
            out var error);

        Assert.True(ok, error);
        Assert.Equal("/music", Assert.Single(matches).ContainerPrefix);
        Assert.Equal("/music", Assert.Single(maps).ContainerPrefix);
    }

    [Fact]
    public void Uses_music_when_libraries_is_not_mounted_and_nothing_was_saved()
    {
        Assert.Equal("/libraries", LibraryPathMapper.ResolveContainer(null, _ => false));
        Assert.Equal("/music", LibraryPathMapper.ResolveContainer(null, path => path == "/music"));
        Assert.Equal("/libraries", LibraryPathMapper.ResolveContainer(null, path => path is "/libraries" or "/music"));
        Assert.Equal("/music", LibraryPathMapper.ResolveContainer(" /music/ "));
    }

    [Fact]
    public void Marks_a_folder_not_found_without_writing_a_token()
    {
        var ok = LibraryPathMapper.TryMatch(
            [@"M:\Music"],
            "/libraries",
            [@"M:\Music\a.flac"],
            _ => false,
            _ => false,
            out var matches,
            out var maps,
            out var error);

        Assert.True(ok, error);
        Assert.Equal(LibraryMapStatus.NotFound, Assert.Single(matches).Status);
        Assert.Empty(maps);
        Assert.DoesNotContain("token", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Shows_the_library_name_instead_of_the_section_key()
    {
        var libraries = new[] { ("3", "Music"), ("1", "Movies") };
        Assert.Equal("Music", LibraryNames.Label("3", null, libraries));
        Assert.Equal("Music", LibraryNames.Label("3", "Music", []));
        Assert.Equal("Saved library", LibraryNames.Label("3", null, []));
        Assert.DoesNotContain("3", LibraryNames.Label("3", null, []), StringComparison.Ordinal);
    }
}

public class LibraryPathPolicyTests
{
    [Theory]
    [InlineData("/library/sections")]
    [InlineData("/library/sections/3/all?type=1&X-Plex-Container-Start=0&X-Plex-Container-Size=5")]
    [InlineData("/library/sections/2/all?type=4&X-Plex-Container-Start=0&X-Plex-Container-Size=100")]
    [InlineData("/library/sections/3/all?type=10&X-Plex-Container-Start=0&X-Plex-Container-Size=5")]
    public void Allows_movie_episode_and_track_samples(string path)
        => Assert.True(LibraryPathPolicy.IsAllowed(HttpMethod.Get, path));

    [Theory]
    [InlineData("PUT", "/library/sections/3/all?type=10")]
    [InlineData("GET", "/library/sections/3/all?type=10&X-Plex-Token=secret")]
    [InlineData("GET", "/library/sections/3/refresh")]
    [InlineData("GET", "/library/sections/3/all?type=2")]
    public void Rejects_writes_tokens_and_other_types(string method, string path)
        => Assert.False(LibraryPathPolicy.IsAllowed(new HttpMethod(method), path));
}

public class LibraryPathSaveTests
{
    private const string Token = "library-path-token-do-not-leak";

    [Fact]
    public async Task Checking_windows_music_and_a_movie_library_does_not_store_the_token()
    {
        using var fixture = new ServiceFixture();
        var root = Path.Combine(fixture.Root, "libraries");
        Directory.CreateDirectory(Path.Combine(root, "music", "Artist"));
        Directory.CreateDirectory(Path.Combine(root, "movies"));
        File.WriteAllBytes(Path.Combine(root, "music", "Artist", "01.flac"), [1]);
        File.WriteAllBytes(Path.Combine(root, "movies", "A.mkv"), [1]);
        fixture.Handler.Responder = request =>
        {
            if (request.Uri.AbsolutePath == "/library/sections")
            {
                return HttpJson.Json("""
                    {"MediaContainer":{"Directory":[
                      {"key":"1","type":"movie","title":"Movies","Location":[{"path":"/mnt/user/media/movies"}]},
                      {"key":"3","type":"artist","title":"Music","Location":[{"path":"M:\\Music"}]}
                    ]}}
                    """);
            }

            if (request.Uri.AbsolutePath == "/library/sections/1/all")
                return HttpJson.Json(Item("movie", "21", "/mnt/user/media/movies/A.mkv"));
            if (request.Uri.AbsolutePath == "/library/sections/3/all")
                return HttpJson.Json(Item("track", "31", "M:\\Music\\Artist\\01.flac"));
            return HttpJson.Json("{}", HttpStatusCode.NotFound);
        };

        var saved = await fixture.Get<PlexServerService>().SaveAsync(null, "Shelf", "http://plex.test:32400", Token);
        var service = Service(fixture);
        var report = await service.CheckAsync(saved.Id, root);

        Assert.Equal(root, report.ContainerFolder);
        Assert.Contains(report.Sections, section => section.Title == "Movies" && section.Status == LibraryMapStatus.Ok);
        Assert.Contains(report.Sections, section => section.Title == "Music" && section.Status == LibraryMapStatus.Ok);
        Assert.Contains(report.Maps, map => map.PlexPrefix == "/mnt/user/media/movies" && map.ContainerPrefix == Path.Combine(root, "movies"));
        Assert.Contains(report.Maps, map => map.PlexPrefix == @"M:\Music" && string.Equals(map.ContainerPrefix, Path.Combine(root, "music"), StringComparison.Ordinal));
        Assert.All(fixture.Handler.Requests, request => Assert.DoesNotContain(Token, request.Uri.Query, StringComparison.Ordinal));
        Assert.DoesNotContain(fixture.Logs.Logger.Messages, message => message.Contains(Token, StringComparison.Ordinal));
        fixture.AssertNoSecret(Token);
    }

    [Fact]
    public async Task Adding_the_libraries_mount_keeps_saved_prefixes_tokens_slots_and_keys()
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
        Dictionary<string, byte[]> keyBytes = [];
        var maps = LyricsPaths.ToJson([new LyricsPathMap("/mnt/user/media/music", "/music")]);
        var stamp = new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);
        int serverId;
        await using (var scope = provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var migrator = db.Database.GetService<IMigrator>() ?? throw new InvalidOperationException("Missing migrator.");
            await migrator.MigrateAsync("20261116025816_LyricsLibraryMap");
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
                INSERT INTO LyricsSettings (PlexServerConnectionId, PathMapsJson, AutoCheckEnabled, AutoCheckIntervalHours, LastAutoCheckUtc, ContainerFolder, UseManualMaps, MusicSectionKey, LocationMatchesJson)
                VALUES ({serverId}, {maps}, {true}, {12}, {stamp}, {"/music"}, {false}, {"3"}, {null})
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
            Assert.Equal("3", row.MusicSectionKey);
            Assert.Null(row.MusicSectionTitle);
            Assert.Equal("/music", row.ContainerFolder);
            Assert.False(row.UseManualMaps);
            Assert.True(row.AutoCheckEnabled);
            Assert.Equal(stamp, row.LastAutoCheckUtc);
            Assert.Equal(maps, mount.PathMapsJson);
            Assert.Equal("/music", mount.ContainerFolder);
            Assert.False(mount.UseManualMaps);
            Assert.Null(mount.SectionMatchesJson);
            Assert.Equal(2, server.Slot);
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

    private static LibraryPathService Service(ServiceFixture fixture)
    {
        var transport = new HandlerTransport(fixture.Handler);
        return new LibraryPathService(
            fixture.Provider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>(),
            fixture.Provider.GetRequiredService<ISecretProtector>(),
            fixture.Get<PlexReadClient>(),
            new LibraryFileClient(transport, new AllowAllAddressGuard(), NullLogger<LibraryFileClient>.Instance),
            new StubAdmin("admin"));
    }

    private static string Item(string type, string key, string file)
        => "{\"MediaContainer\":{\"size\":1,\"totalSize\":1,\"Metadata\":[{\"ratingKey\":\"" + key + "\",\"type\":\"" + type + "\",\"title\":\"Item\",\"Media\":[{\"Part\":[{\"file\":\"" + file.Replace("\\", "\\\\") + "\"}]}]}]}}";

    private sealed class StubAdmin(string id) : IAdminGate
    {
        public Task<string> RequireUserIdAsync(CancellationToken cancellationToken) => Task.FromResult(id);
    }
}
