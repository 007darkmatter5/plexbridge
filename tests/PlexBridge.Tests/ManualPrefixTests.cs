using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using PlexBridge.Data;
using PlexBridge.Libraries;
using PlexBridge.Lyrics;
using PlexBridge.Plex;
using PlexBridge.Security;
using PlexBridge.Services;

namespace PlexBridge.Tests;

public class ManualPrefixTests
{
    [Fact]
    public void A_synced_copy_is_ok_when_the_container_folder_holds_the_tracks()
    {
        var root = "/libraries/music";
        var sample = @"M:\Music\Artist\Album\song.flac";
        var ok = ManualPrefixCheck.TryCheck(
            [new LyricsPathMap(@"M:\Music", root)],
            [sample],
            directoryExists: path => path == root,
            fileExists: path => path.Replace('\\', '/').EndsWith("/libraries/music/Artist/Album/song.flac", StringComparison.Ordinal),
            canWrite: _ => true,
            out var matches,
            out var error);

        Assert.True(ok, error);
        var match = Assert.Single(matches);
        Assert.Equal(LyricsMapStatus.Ok, match.Status);
        Assert.Equal(root, match.ContainerPrefix);
        Assert.Equal(1, match.SamplesFound);
        var line = PathMapText.Line(match.PlexLocation, match.ContainerPrefix, match.Status);
        Assert.Equal(@"M:\Music → /libraries/music · OK", line);
        Assert.DoesNotContain("Not found", line, StringComparison.Ordinal);
    }

    [Fact]
    public void A_missing_container_folder_is_not_reported_as_a_missing_plex_path()
    {
        var called = false;
        var ok = ManualPrefixCheck.TryCheck(
            [new LyricsPathMap(@"M:\Music", "/libraries/music")],
            [@"M:\Music\song.flac"],
            _ => false,
            _ => true,
            _ =>
            {
                called = true;
                return true;
            },
            out var matches,
            out var error);

        Assert.True(ok, error);
        var match = Assert.Single(matches);
        Assert.Equal(LyricsMapStatus.ContainerMissing, match.Status);
        Assert.Equal("/libraries/music", match.ContainerPrefix);
        Assert.False(called);
        var line = PathMapText.Line(match.PlexLocation, match.ContainerPrefix, match.Status);
        Assert.DoesNotContain("Not found", line, StringComparison.Ordinal);
    }

    [Fact]
    public void A_container_folder_that_cannot_be_written_is_not_ok()
    {
        var ok = ManualPrefixCheck.TryCheck(
            [new LyricsPathMap(@"M:\Music", "/libraries/music")],
            [@"M:\Music\song.flac"],
            _ => true,
            _ => true,
            _ => false,
            out var matches,
            out var error);

        Assert.True(ok, error);
        Assert.Equal(LyricsMapStatus.NotWritable, Assert.Single(matches).Status);
    }

    [Fact]
    public void No_translated_file_is_not_ok()
    {
        var ok = ManualPrefixCheck.TryCheck(
            [new LyricsPathMap(@"M:\Music", "/libraries/music")],
            [@"M:\Music\song.flac"],
            _ => true,
            _ => false,
            _ => true,
            out var matches,
            out var error);

        Assert.True(ok, error);
        var match = Assert.Single(matches);
        Assert.Equal(LyricsMapStatus.NoFiles, match.Status);
        Assert.Equal(0, match.SamplesFound);
        Assert.Equal(1, match.SamplesChecked);
    }

    [Fact]
    public void The_manual_switch_decides_which_saved_prefix_is_used()
    {
        var json = LyricsPaths.ToJson([new LyricsPathMap(@"M:\Music", "/libraries/music"), new LyricsPathMap(@"M:\Movies", "/libraries/movies")]);
        var missed = LyricsLibraryMapper.MatchesToJson(
            [new LyricsLocationMatch(@"M:\Music", null, LyricsMapStatus.NotFound, 5, 0)]);
        var setting = new LyricsSetting { UseManualMaps = false, PathMapsJson = json, LocationMatchesJson = missed };
        var mount = new LibraryMount { UseManualMaps = false, PathMapsJson = json, SectionMatchesJson = null };

        var off = LibraryMountStore.ReadActiveMaps(mount, setting);
        Assert.Equal(@"M:\Movies", Assert.Single(off).PlexPrefix);
        Assert.Equal(2, LibraryMountStore.ReadMaps(mount, setting).Count);

        mount.UseManualMaps = true;
        setting.UseManualMaps = true;
        var on = LibraryMountStore.ReadActiveMaps(mount, setting);
        Assert.Equal(2, on.Count);
        Assert.Contains(on, map => map.PlexPrefix == @"M:\Music" && map.ContainerPrefix == "/libraries/music");
    }

    [Fact]
    public void A_saved_prefix_with_no_check_stays_in_use()
    {
        var json = LyricsPaths.ToJson([new LyricsPathMap("/mnt/user/media/music", "/music")]);
        var setting = new LyricsSetting { UseManualMaps = false, PathMapsJson = json };
        var mount = new LibraryMount { UseManualMaps = false, PathMapsJson = json };
        Assert.Single(LibraryMountStore.ReadActiveMaps(mount, setting));
    }

    [Fact]
    public async Task Checking_manual_prefixes_reports_ok_for_a_real_container_folder()
    {
        using var fixture = new ServiceFixture();
        var root = Path.Combine(fixture.Root, "music");
        Directory.CreateDirectory(Path.Combine(root, "Artist", "Album"));
        var audio = Path.Combine(root, "Artist", "Album", "song.flac");
        File.WriteAllBytes(audio, [1, 2, 3]);
        const string token = "manual-check-token-do-not-leak";
        fixture.Handler.Responder = request =>
        {
            if (request.Uri.AbsolutePath == "/library/sections")
                return HttpJson.Json(Sections());
            if (request.Uri.AbsolutePath == "/library/sections/5/all")
                return HttpJson.Json(Page(@"M:\Music\Artist\Album\song.flac"));
            return HttpJson.Json("{}", System.Net.HttpStatusCode.NotFound);
        };
        var saved = await fixture.Get<PlexServerService>().SaveAsync(null, "Shelf", "http://plex.test:32400", token);
        var page = Page(fixture);
        var report = await page.CheckManualAsync(saved.Id, "5", [new LyricsMapInput { PlexPrefix = @"M:\Music", ContainerPrefix = root }]);

        var match = Assert.Single(report.Matches);
        Assert.Equal(LyricsMapStatus.Ok, match.Status);
        Assert.Equal(root, match.ContainerPrefix);
        Assert.True(match.SamplesFound >= 1);
        Assert.DoesNotContain("Not found", PathMapText.Line(match.PlexLocation, match.ContainerPrefix, match.Status), StringComparison.Ordinal);
        Assert.All(fixture.Handler.Requests, request => Assert.DoesNotContain("X-Plex-Token", request.Uri.Query, StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(fixture.Logs.Logger.Messages, message => message.Contains(token, StringComparison.Ordinal));
        fixture.AssertNoSecret(token);
        Assert.Equal(new byte[] { 1, 2, 3 }, await File.ReadAllBytesAsync(audio));
    }

    [Fact]
    public async Task Saving_a_missed_library_keeps_the_prefix_but_does_not_use_it_while_manual_is_off()
    {
        using var fixture = new ServiceFixture();
        const string token = "manual-off-token-do-not-leak";
        fixture.Handler.Responder = request =>
        {
            if (request.Uri.AbsolutePath == "/library/sections")
                return HttpJson.Json(Sections());
            if (request.Uri.AbsolutePath == "/library/sections/5/all")
                return HttpJson.Json(Page("/mnt/user/media/music/A/01.flac"));
            return HttpJson.Json("{}", System.Net.HttpStatusCode.NotFound);
        };
        var saved = await fixture.Get<PlexServerService>().SaveAsync(null, "Shelf", "http://plex.test:32400", token);
        var page = Page(fixture);
        await page.SaveAsync(new LyricsSettingsInput
        {
            ServerId = saved.Id,
            UseManualMaps = true,
            Maps = [new LyricsMapInput { PlexPrefix = @"M:\Music", ContainerPrefix = "/libraries/music" }]
        });
        await page.SaveLibraryAsync(new LyricsSettingsInput
        {
            ServerId = saved.Id,
            SectionKey = "5",
            ContainerFolder = "/libraries"
        });

        await using var scope = fixture.Provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var setting = await db.LyricsSettings.AsNoTracking().SingleAsync();
        var mount = await db.LibraryMounts.AsNoTracking().SingleAsync();
        Assert.False(setting.UseManualMaps);
        Assert.False(mount.UseManualMaps);
        Assert.Contains("M:\\\\Music", setting.PathMapsJson);
        Assert.Contains("Not found", setting.LocationMatchesJson);
        Assert.Empty(LibraryMountStore.ReadActiveMaps(mount, setting));
        Assert.Equal(2, setting.SyncIntervalMinutes);
        Assert.Equal(30, setting.SyncWindowMinutes);
        fixture.AssertNoSecret(token);
    }

    private static LyricsPageService Page(ServiceFixture fixture)
    {
        var transport = new HandlerTransport(fixture.Handler);
        var lyrics = new PlexLyricsClient(transport, new ForwardingPlexWriteTransport(transport), new AllowAllAddressGuard(), NullLogger<PlexLyricsClient>.Instance);
        return new LyricsPageService(
            fixture.Get<IDbContextFactory<ApplicationDbContext>>(),
            fixture.Get<ISecretProtector>(),
            fixture.Get<PlexReadClient>(),
            lyrics,
            new StubAdmin("admin"),
            new LyricsJobs(fixture.Provider.GetRequiredService<IServiceScopeFactory>(), NullLogger<LyricsJobs>.Instance),
            fixture.SyncQueue(),
            fixture.SyncRefresh(lyrics));
    }

    private static string Sections()
        => "{\"MediaContainer\":{\"Directory\":[{\"key\":\"5\",\"type\":\"artist\",\"title\":\"Music\",\"Location\":[{\"path\":\"M:\\\\Music\"}]}]}}";

    private static string Page(string file)
        => "{\"MediaContainer\":{\"size\":1,\"totalSize\":1,\"Metadata\":[{\"ratingKey\":\"11\",\"type\":\"track\",\"title\":\"Song\",\"grandparentTitle\":\"Artist\",\"parentTitle\":\"Album\",\"parentRatingKey\":\"2\",\"duration\":1000,\"Media\":[{\"Part\":[{\"file\":\"" + file.Replace("\\", "\\\\") + "\"}]}]}]}}";

    private sealed class StubAdmin(string id) : IAdminGate
    {
        public Task<string> RequireUserIdAsync(CancellationToken cancellationToken) => Task.FromResult(id);
    }
}
