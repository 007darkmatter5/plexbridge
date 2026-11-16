using System.Net;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PlexBridge.Data;
using PlexBridge.Lyrics;
using PlexBridge.Plex;
using PlexBridge.Security;
using PlexBridge.Services;

namespace PlexBridge.Tests;

public class LyricsPathTests
{
    [Fact]
    public void Maps_a_linux_prefix_and_keeps_the_rest_of_the_path()
    {
        var maps = new[] { new LyricsPathMap("/data/music", "/music") };
        var ok = LyricsPaths.TryMap("/data/music/Bob Schneider/I'm Good Now/01-Come With Me Tonight.flac", maps, out var path, out var error);
        Assert.True(ok, error);
        Assert.Equal("/music/Bob Schneider/I'm Good Now/01-Come With Me Tonight.flac", path);
    }

    [Fact]
    public void Maps_a_windows_plex_path_onto_a_container_folder()
    {
        var maps = new[] { new LyricsPathMap(@"d:\Music", "/music") };
        var ok = LyricsPaths.TryMap(@"D:\Music\Artist\Album\song.flac", maps, out var path, out var error);
        Assert.True(ok, error);
        Assert.Equal("/music/Artist/Album/song.flac", path);
    }

    [Theory]
    [InlineData("/data/music2/song.flac", "/data/music")]
    [InlineData("/data/musical/song.flac", "/data/music")]
    public void Does_not_match_a_prefix_that_only_shares_characters(string plexPath, string prefix)
    {
        var ok = LyricsPaths.TryMap(plexPath, [new LyricsPathMap(prefix, "/music")], out _, out var error);
        Assert.False(ok);
        Assert.Contains("does not start", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Longest_prefix_wins()
    {
        var maps = new[]
        {
            new LyricsPathMap("/data/music", "/music"),
            new LyricsPathMap("/data/music/classical", "/classical")
        };
        var ok = LyricsPaths.TryMap("/data/music/classical/Bach/01.flac", maps, out var path, out var error);
        Assert.True(ok, error);
        Assert.Equal("/classical/Bach/01.flac", path);
    }

    [Fact]
    public void Rejects_a_path_that_climbs_out_of_the_mapped_folder()
    {
        var ok = LyricsPaths.TryMap("/data/music/../secret/song.flac", [new LyricsPathMap("/data/music", "/music")], out _, out var error);
        Assert.False(ok);
        Assert.DoesNotContain("secret", error);
    }

    [Theory]
    [InlineData("/config")]
    [InlineData("/app")]
    [InlineData("/etc/passwd")]
    [InlineData("/")]
    public void Rejects_container_prefixes_that_are_not_a_music_folder(string container)
    {
        var ok = LyricsPaths.TryNormalize([new LyricsPathMap("/data/music", container)], out _, out var error);
        Assert.False(ok);
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Fact]
    public void Round_trips_saved_maps()
    {
        var maps = new[] { new LyricsPathMap("/mnt/user/media/music", "/music") };
        var again = LyricsPaths.FromJson(LyricsPaths.ToJson(maps));
        Assert.Equal(maps, again);
        Assert.DoesNotContain("token", LyricsPaths.ToJson(maps), StringComparison.OrdinalIgnoreCase);
    }
}

public class LyricsMatchingTests
{
    private static LyricsQuery Query(string title = "Come With Me Tonight", string artist = "Bob Schneider", string album = "I'm Good Now", int duration = 233)
        => new(title, artist, album, duration);

    private static LrclibRecord Record(string title, string artist, string album, int duration, string? synced, string? plain, bool instrumental = false)
        => new(title, artist, album, duration, instrumental, plain, synced);

    [Fact]
    public void Prefers_synced_lyrics_when_the_duration_is_close()
    {
        var plain = Record("Come With Me Tonight", "Bob Schneider", "I'm Good Now", 233, null, "plain line");
        var synced = Record("Come With Me Tonight", "Bob Schneider", "I'm Good Now", 235, "[00:12.00] line", "plain line");
        var choice = LyricsMatching.Choose(Query(), [plain, synced]);
        Assert.Equal(LyricsChoiceKind.Match, choice.Kind);
        Assert.Equal(synced, choice.Record);
    }

    [Fact]
    public void Rejects_a_duration_that_is_too_far_off()
    {
        var other = Record("Come With Me Tonight", "Bob Schneider", "I'm Good Now", 400, "[00:01.00] no", "no");
        var choice = LyricsMatching.Choose(Query(), [other]);
        Assert.Equal(LyricsChoiceKind.None, choice.Kind);
    }

    [Fact]
    public void Matches_a_remaster_title_and_a_featured_artist()
    {
        var record = Record("Come With Me Tonight (Remastered)", "Bob Schneider feat. Someone", "I'm Good Now (Deluxe)", 233, "[00:01.00] line", "line");
        var choice = LyricsMatching.Choose(Query(), [record]);
        Assert.Equal(record, choice.Record);
    }

    [Fact]
    public void Does_not_match_a_different_artist()
    {
        var record = Record("Come With Me Tonight", "Somebody Else", "I'm Good Now", 233, "[00:01.00] line", "line");
        Assert.Equal(LyricsChoiceKind.None, LyricsMatching.Choose(Query(), [record]).Kind);
    }

    [Fact]
    public void Skips_an_instrumental_when_that_is_the_only_hit()
    {
        var record = Record("Come With Me Tonight", "Bob Schneider", "I'm Good Now", 233, null, null, instrumental: true);
        Assert.Equal(LyricsChoiceKind.Instrumental, LyricsMatching.Choose(Query(), [record]).Kind);
    }

    [Fact]
    public void Uses_the_vocal_version_when_an_instrumental_is_also_returned()
    {
        var instrumental = Record("Come With Me Tonight", "Bob Schneider", "I'm Good Now", 233, null, null, instrumental: true);
        var vocal = Record("Come With Me Tonight", "Bob Schneider", "I'm Good Now", 233, "[00:01.00] line", "line");
        Assert.Equal(vocal, LyricsMatching.Choose(Query(), [instrumental, vocal]).Record);
    }

    [Fact]
    public void Requires_the_album_for_various_artists()
    {
        var wrong = Record("Intro", "Various Artists", "Other Album", 90, "[00:01.00] line", "line");
        var right = Record("Intro", "Various Artists", "I'm Good Now", 90, "[00:01.00] line", "line");
        var query = Query("Intro", "Various Artists", "I'm Good Now", 90);
        Assert.Equal(LyricsChoiceKind.None, LyricsMatching.Choose(query, [wrong]).Kind);
        Assert.Equal(right, LyricsMatching.Choose(query, [wrong, right]).Record);
    }
}

public class LyricsSidecarTests
{
    [Fact]
    public void Writes_synced_lyrics_as_utf8_lrc_and_plain_lyrics_as_txt()
    {
        var root = NewRoot();
        try
        {
            var audio = Path.Combine(root, "01-Come With Me Tonight.flac");
            File.WriteAllBytes(audio, [0x66, 0x4c, 0x61, 0x43]);
            var lrc = LyricsSidecar.FormatLrc("Bob Schneider", "I'm Good Now", "Come With Me Tonight", 233, "[00:12.00] Meet me there");
            Assert.True(LyricsSidecar.TryWrite(audio, true, lrc, out var written, out var error), error);
            Assert.Equal(Path.ChangeExtension(audio, ".lrc"), written);
            var bytes = File.ReadAllBytes(written);
            Assert.False(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF);
            var text = Encoding.UTF8.GetString(bytes);
            Assert.Contains("[ar:Bob Schneider]", text);
            Assert.Contains("[00:12.00] Meet me there", text);
            Assert.Contains("[length:3:53]", text);

            var other = Path.Combine(root, "02-Medicine.mp3");
            File.WriteAllBytes(other, [0]);
            Assert.True(LyricsSidecar.TryWrite(other, false, LyricsSidecar.FormatPlain("Take the medicine"), out var txt, out error), error);
            Assert.EndsWith(".txt", txt);
            Assert.False(File.Exists(Path.ChangeExtension(other, ".lrc")));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void Does_not_overwrite_an_existing_lrc_or_txt()
    {
        var root = NewRoot();
        try
        {
            var audio = Path.Combine(root, "song.flac");
            File.WriteAllBytes(audio, [1]);
            var existing = Path.ChangeExtension(audio, ".txt");
            File.WriteAllText(existing, "keep me");
            Assert.True(LyricsSidecar.HasExisting(audio));
            Assert.False(LyricsSidecar.TryWrite(audio, true, "[00:01.00] new", out _, out var error));
            Assert.Contains("already", error, StringComparison.OrdinalIgnoreCase);
            Assert.Equal("keep me", File.ReadAllText(existing));
            Assert.False(File.Exists(Path.ChangeExtension(audio, ".lrc")));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void A_normal_folder_is_writable()
    {
        var root = NewRoot();
        try
        {
            Assert.True(DirectoryWrite.CanWrite(root));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    private static string NewRoot()
    {
        var path = Path.Combine(Path.GetTempPath(), "plexbridge-lyrics", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(path);
        return path;
    }
}

public class PlexLyricsPolicyTests
{
    [Theory]
    [InlineData("GET", "/library/sections")]
    [InlineData("GET", "/library/sections/2/all?type=10&X-Plex-Container-Start=0&X-Plex-Container-Size=100")]
    [InlineData("GET", "/library/sections/2/all?type=8&X-Plex-Container-Start=0&X-Plex-Container-Size=100")]
    [InlineData("GET", "/library/sections/2/all?type=10&artist.id=8&album.id=9&sort=addedAt:desc&X-Plex-Container-Size=100")]
    [InlineData("GET", "/library/metadata/123")]
    public void Allows_music_reads(string method, string path)
        => Assert.True(PlexLyricsPolicy.IsReadAllowed(new HttpMethod(method), path));

    [Theory]
    [InlineData("GET", "/library/sections/2/all")]
    [InlineData("GET", "/library/sections/2/all?type=1")]
    [InlineData("GET", "/library/sections/2/all?type=10&sort=titleSort")]
    [InlineData("GET", "/library/sections/2/refresh")]
    [InlineData("GET", "/library/metadata/123?X-Plex-Token=abc")]
    [InlineData("PUT", "/library/metadata/55/refresh")]
    public void Rejects_reads_outside_a_lyrics_lookup(string method, string path)
        => Assert.False(PlexLyricsPolicy.IsReadAllowed(new HttpMethod(method), path));

    [Fact]
    public void Allows_only_a_single_item_metadata_refresh()
    {
        Assert.True(PlexLyricsPolicy.IsWriteAllowed(HttpMethod.Put, "/library/metadata/55/refresh"));
        Assert.False(PlexLyricsPolicy.IsWriteAllowed(HttpMethod.Put, "/library/metadata/55/refresh?force=1"));
        Assert.False(PlexLyricsPolicy.IsWriteAllowed(HttpMethod.Put, "/library/sections/1/refresh"));
        Assert.False(PlexLyricsPolicy.IsWriteAllowed(HttpMethod.Get, "/library/metadata/55/refresh"));
        Assert.False(PlexLyricsPolicy.IsWriteAllowed(HttpMethod.Put, "/library/metadata/55"));
        Assert.False(PlexWritePolicy.IsAllowed(HttpMethod.Put, "/library/metadata/55/refresh"));
    }
}

public class PlexTrackParserTests
{
    [Fact]
    public void Reads_a_local_lyrics_stream_and_the_file_path()
    {
        const string json = """
            {"MediaContainer":{"size":1,"totalSize":4,"Metadata":[{
              "ratingKey":"123","type":"track","title":"Come With Me Tonight",
              "grandparentTitle":"Bob Schneider","parentTitle":"I'm Good Now",
              "parentRatingKey":"55","grandparentRatingKey":"9",
              "duration":233400,"addedAt":1700000000,
              "Media":[{"Part":[{"file":"/data/music/Bob Schneider/I'm Good Now/01.flac","Stream":[
                {"id":1,"streamType":2,"codec":"flac"},
                {"id":2,"streamType":4,"codec":"lrc","provider":"com.plexapp.agents.localmedia"}
              ]}]}]
            }]}}
            """;
        var item = Assert.Single(PlexTrackParser.Parse(json).Items);
        Assert.Equal(233, item.DurationSeconds);
        Assert.Equal("/data/music/Bob Schneider/I'm Good Now/01.flac", item.FilePath);
        Assert.True(item.HasLyricsStream);
        Assert.True(item.HasLocalLyrics);
        Assert.Equal("lrc", item.LyricsCodec);
        Assert.Equal("55", item.AlbumKey);
        Assert.Equal(4, PlexTrackParser.Parse(json).Total);
    }

    [Fact]
    public void Treats_a_string_stream_type_and_lyricfind_as_lyrics_that_are_not_local()
    {
        const string json = """
            {"MediaContainer":{"Metadata":{"ratingKey":"8","type":"track","title":"Song","grandparentTitle":"Artist","parentTitle":"Album","duration":1000,
              "Media":{"Part":{"file":"/music/a.mp3","Stream":{"streamType":"4","codec":"txt","provider":"com.plexapp.agents.lyricfind"}}}}}}
            """;
        var item = Assert.Single(PlexTrackParser.Parse(json).Items);
        Assert.True(item.HasLyricsStream);
        Assert.False(item.HasLocalLyrics);
        Assert.Equal("txt", item.LyricsCodec);
    }
}

public class LrclibClientTests
{
    private const string Token = "lyrics-token-do-not-leak-77";

    [Fact]
    public async Task Uses_get_first_and_falls_back_to_a_scored_search()
    {
        var handler = new ScriptHandler();
        handler.Responder = request =>
        {
            if (request.Uri.AbsolutePath == "/api/get")
                return HttpJson.Json("""{"statusCode":404,"name":"TrackNotFound","message":"Failed to find specified track"}""", HttpStatusCode.NotFound);
            Assert.Equal("/api/search", request.Uri.AbsolutePath);
            return HttpJson.Json("""
                [
                  {"trackName":"Other","artistName":"Other","albumName":"Other","duration":10,"instrumental":false,"plainLyrics":"no","syncedLyrics":"[00:01.00] no"},
                  {"trackName":"Come With Me Tonight","artistName":"Bob Schneider","albumName":"I'm Good Now","duration":233,"instrumental":false,"plainLyrics":"plain","syncedLyrics":"[00:12.00] line"}
                ]
                """);
        };
        var capture = new LrclibCapture(handler);
        var client = Client(capture);
        var result = await client.FindAsync(new LyricsQuery("Come With Me Tonight", "Bob Schneider", "I'm Good Now", 233), CancellationToken.None);
        Assert.Equal(LyricsLookupKind.Synced, result.Kind);
        Assert.Contains("[00:12.00]", result.Body);
        Assert.Equal(2, handler.Requests.Count);
        Assert.StartsWith("PlexBridge/", LrclibClient.UserAgent);
        Assert.Contains("https://github.com/007darkmatter5/plexbridge", LrclibClient.UserAgent);
        Assert.All(handler.Requests, request =>
        {
            Assert.Null(request.Token);
            Assert.DoesNotContain("X-Plex-Token", request.Uri.Query, StringComparison.OrdinalIgnoreCase);
        });
        Assert.Equal(2, capture.Agents.Count);
        Assert.All(capture.Agents, agent => Assert.Equal(LrclibClient.UserAgent, agent));
    }

    [Fact]
    public async Task Stops_when_get_says_the_track_is_instrumental()
    {
        var handler = new ScriptHandler();
        handler.Responder = _ => HttpJson.Json("""
            {"trackName":"Intro","artistName":"Bob Schneider","albumName":"I'm Good Now","duration":90,"instrumental":true,"plainLyrics":null,"syncedLyrics":null}
            """);
        var client = Client(handler);
        var result = await client.FindAsync(new LyricsQuery("Intro", "Bob Schneider", "I'm Good Now", 90), CancellationToken.None);
        Assert.Equal(LyricsLookupKind.Instrumental, result.Kind);
        Assert.Single(handler.Requests);
        Assert.Equal("/api/get", handler.Requests[0].Uri.AbsolutePath);
    }

    [Fact]
    public async Task Keeps_at_most_two_lookups_in_flight()
    {
        var gate = new CountingHandler();
        var client = Client(gate);
        await Task.WhenAll(Enumerable.Range(0, 6).Select(index =>
            client.FindAsync(new LyricsQuery("Song " + index, "Artist", "Album", 100), CancellationToken.None)));
        Assert.InRange(gate.Max, 1, 2);
        Assert.DoesNotContain(Token, LrclibClient.UserAgent);
    }

    private sealed class CountingHandler : HttpMessageHandler
    {
        private int _inFlight;
        public int Max { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var now = Interlocked.Increment(ref _inFlight);
            lock (this)
                Max = Math.Max(Max, now);
            try
            {
                await Task.Delay(40, cancellationToken);
                return HttpJson.Json("""{"statusCode":404,"name":"TrackNotFound","message":"no"}""", HttpStatusCode.NotFound);
            }
            finally
            {
                Interlocked.Decrement(ref _inFlight);
            }
        }
    }

    private static LrclibClient Client(HttpMessageHandler handler)
        => Client(new LrclibCapture(handler));

    private static LrclibClient Client(LrclibCapture capture)
        => new(capture, TimeProvider.System, NullLogger<LrclibClient>.Instance, new LrclibLimits
        {
            MinInterval = TimeSpan.Zero,
            MaxInFlight = 2,
            BaseUri = new Uri("https://lrclib.test/")
        });
}

public sealed class LrclibCapture(HttpMessageHandler handler) : ILrclibTransport
{
    public List<string> Agents { get; } = [];
    private readonly HttpClient _client = new(handler, disposeHandler: false);

    public Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        lock (Agents)
            Agents.Add(request.Headers.UserAgent.ToString());
        Assert.False(request.Headers.Contains("X-Plex-Token"));
        return _client.SendAsync(request, cancellationToken);
    }
}

public class LyricsWorkflowTests
{
    private const string Token = "lyrics-token-do-not-leak-77";

    [Fact]
    public async Task Scan_and_write_prefers_synced_lrc_then_refreshes_the_album()
    {
        var root = Path.Combine(Path.GetTempPath(), "plexbridge-lyrics", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(root);
        var audio = Path.Combine(root, "01-Come With Me Tonight.flac");
        File.WriteAllBytes(audio, [1, 2, 3]);
        var handler = new ScriptHandler();
        var albumRefreshed = false;
        handler.Responder = request =>
        {
            if (request.Uri.Host == "lrclib.test")
            {
                Assert.Null(request.Token);
                Assert.DoesNotContain(Token, request.Uri.ToString());
                return HttpJson.Json("""
                    {"trackName":"Come With Me Tonight","artistName":"Bob Schneider","albumName":"I'm Good Now","duration":233,"instrumental":false,"plainLyrics":"plain line","syncedLyrics":"[00:12.00] Meet me there"}
                    """);
            }

            Assert.Equal(Token, request.Token);
            Assert.DoesNotContain("X-Plex-Token", request.Uri.Query, StringComparison.OrdinalIgnoreCase);
            if (request.Method == HttpMethod.Put)
            {
                Assert.Equal("/library/metadata/55/refresh", request.Uri.AbsolutePath);
                albumRefreshed = true;
                return HttpJson.Json("""{"MediaContainer":{}}""");
            }

            if (request.Uri.AbsolutePath == "/library/metadata/123")
                return HttpJson.Json(Track("/data/music/01-Come With Me Tonight.flac", albumRefreshed));
            return HttpJson.Json(Page("/data/music/01-Come With Me Tonight.flac", lyrics: false));
        };

        var logs = new SpyLoggerProvider();
        var workflow = Workflow(handler, logs);
        var rows = await workflow.ScanAsync(
            new Uri("http://plex.test:32400"),
            Token,
            "2",
            null,
            null,
            [new LyricsPathMap("/data/music", root)],
            null,
            false,
            null,
            null,
            CancellationToken.None);
        var row = Assert.Single(rows);
        Assert.Equal(LyricsStatus.Synced, row.Status);
        Assert.True(row.Selected);
        Assert.Equal(audio, row.ContainerPath);

        await workflow.PublishAsync(new Uri("http://plex.test:32400"), Token, rows, null, CancellationToken.None);
        Assert.True(File.Exists(Path.ChangeExtension(audio, ".lrc")));
        Assert.False(File.Exists(Path.ChangeExtension(audio, ".txt")));
        Assert.Contains("Plex shows a local lyrics stream", row.ApplyResult);
        Assert.Contains(handler.Requests, request => request.Method == HttpMethod.Put && request.Uri.AbsolutePath == "/library/metadata/55/refresh");
        Assert.DoesNotContain(handler.Requests, request => request.Uri.AbsolutePath.Contains("/library/sections/", StringComparison.Ordinal) && request.Uri.AbsolutePath.Contains("refresh", StringComparison.Ordinal));
        Assert.DoesNotContain(handler.Requests, request => request.Method == HttpMethod.Put && request.Uri.AbsolutePath == "/library/metadata/123/refresh");
        Assert.DoesNotContain(logs.Logger.Messages, message => message.Contains(Token, StringComparison.Ordinal));
        Directory.Delete(root, true);
    }

    [Fact]
    public async Task Refreshes_the_track_when_the_album_refresh_does_not_add_a_lyrics_stream()
    {
        var root = Path.Combine(Path.GetTempPath(), "plexbridge-lyrics", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(root);
        File.WriteAllBytes(Path.Combine(root, "song.flac"), [1]);
        var handler = new ScriptHandler
        {
            Responder = request =>
            {
                if (request.Uri.Host == "lrclib.test")
                    return HttpJson.Json("""{"trackName":"Song","artistName":"Artist","albumName":"Album","duration":100,"instrumental":false,"plainLyrics":"words","syncedLyrics":null}""");
                if (request.Method == HttpMethod.Put)
                    return HttpJson.Json("""{"MediaContainer":{}}""");
                if (request.Uri.AbsolutePath == "/library/metadata/123")
                    return HttpJson.Json(Track("/library/song.flac", lyrics: false, title: "Song", artist: "Artist", album: "Album"));
                return HttpJson.Json(Page("/library/song.flac", lyrics: false, title: "Song", artist: "Artist", album: "Album", duration: 100000));
            }
        };
        var workflow = Workflow(handler, new SpyLoggerProvider());
        var maps = new[] { new LyricsPathMap("/library", root) };
        var rows = await workflow.ScanAsync(new Uri("http://plex.test:32400"), Token, "2", null, null, maps, null, false, null, null, CancellationToken.None);
        Assert.Equal(LyricsStatus.Plain, Assert.Single(rows).Status);
        await workflow.PublishAsync(new Uri("http://plex.test:32400"), Token, rows, null, CancellationToken.None);
        Assert.True(File.Exists(Path.Combine(root, "song.txt")));
        Assert.Contains(handler.Requests, request => request.Method == HttpMethod.Put && request.Uri.AbsolutePath == "/library/metadata/55/refresh");
        Assert.Contains(handler.Requests, request => request.Method == HttpMethod.Put && request.Uri.AbsolutePath == "/library/metadata/123/refresh");
        Assert.Contains("not on the track yet", rows[0].ApplyResult);
        Directory.Delete(root, true);
    }

    [Theory]
    [InlineData("stream")]
    [InlineData("sidecar")]
    [InlineData("missing")]
    [InlineData("locked")]
    public async Task Skips_tracks_that_must_not_be_written(string mode)
    {
        var root = Path.Combine(Path.GetTempPath(), "plexbridge-lyrics", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(root);
        var audio = Path.Combine(root, "song.flac");
        if (mode != "missing")
            File.WriteAllBytes(audio, [1]);
        if (mode == "sidecar")
            File.WriteAllText(Path.ChangeExtension(audio, ".lrc"), "[00:01.00] keep");
        var handler = new ScriptHandler
        {
            Responder = request =>
            {
                if (request.Uri.Host == "lrclib.test")
                    throw new InvalidOperationException("LRCLIB should not be called");
                return HttpJson.Json(Page("/library/song.flac", lyrics: mode == "stream"));
            }
        };
        var maps = mode == "locked" || mode == "missing" || mode == "sidecar" || mode == "stream"
            ? new[] { new LyricsPathMap("/library", root) }
            : Array.Empty<LyricsPathMap>();
        var workflow = Workflow(handler, new SpyLoggerProvider());
        var rows = await workflow.ScanAsync(
            new Uri("http://plex.test:32400"),
            Token,
            "2",
            null,
            null,
            maps,
            null,
            false,
            null,
            mode == "locked" ? _ => false : null,
            CancellationToken.None);
        var row = Assert.Single(rows);
        Assert.False(row.CanSelect);
        var expected = mode switch
        {
            "stream" => LyricsStatus.AlreadyHas,
            "sidecar" => LyricsStatus.AlreadyHas,
            "missing" => LyricsStatus.NotReachable,
            _ => LyricsStatus.NotWritable
        };
        Assert.Equal(expected, row.Status);
        Assert.DoesNotContain(handler.Requests, request => request.Uri.Host == "lrclib.test");
        Directory.Delete(root, true);
    }

    private static LyricsWorkflow Workflow(ScriptHandler handler, SpyLoggerProvider logs)
    {
        var transport = new HandlerTransport(handler);
        var plex = new PlexLyricsClient(transport, new ForwardingPlexWriteTransport(transport), new AllowAllAddressGuard(), new Adapter<PlexLyricsClient>(logs.Logger));
        var lrclib = new LrclibClient(new LrclibCapture(handler), TimeProvider.System, new Adapter<LrclibClient>(logs.Logger), new LrclibLimits
        {
            MinInterval = TimeSpan.Zero,
            MaxInFlight = 2,
            BaseUri = new Uri("https://lrclib.test/")
        });
        return new LyricsWorkflow(plex, lrclib, new LyricsRefreshTiming
        {
            FirstWait = TimeSpan.Zero,
            SecondWait = TimeSpan.Zero,
            Delay = (_, _) => Task.CompletedTask
        }, new Adapter<LyricsWorkflow>(logs.Logger));
    }

    private static string Page(string file, bool lyrics, string title = "Come With Me Tonight", string artist = "Bob Schneider", string album = "I'm Good Now", int duration = 233000)
        => "{\"MediaContainer\":{\"size\":1,\"totalSize\":1,\"Metadata\":[" + Item(file, lyrics, title, artist, album, duration) + "]}}";

    private static string Track(string file, bool lyrics, string title = "Come With Me Tonight", string artist = "Bob Schneider", string album = "I'm Good Now")
        => "{\"MediaContainer\":{\"size\":1,\"Metadata\":[" + Item(file, lyrics, title, artist, album, 233000) + "]}}";

    private static string Item(string file, bool lyrics, string title, string artist, string album, int duration)
    {
        var stream = lyrics
            ? ",{\"id\":9,\"streamType\":4,\"codec\":\"lrc\",\"provider\":\"com.plexapp.agents.localmedia\"}"
            : "";
        return "{\"ratingKey\":\"123\",\"type\":\"track\",\"title\":\"" + title + "\",\"grandparentTitle\":\"" + artist + "\",\"parentTitle\":\"" + album + "\",\"parentRatingKey\":\"55\",\"grandparentRatingKey\":\"9\",\"duration\":" + duration + ",\"addedAt\":1700000000,\"Media\":[{\"Part\":[{\"file\":\"" + file + "\",\"Stream\":[{\"id\":1,\"streamType\":2,\"codec\":\"flac\"}" + stream + "]}]}]}";
    }

    private sealed class Adapter<T>(SpyLogger inner) : ILogger<T>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => inner.BeginScope(state);
        public bool IsEnabled(LogLevel logLevel) => inner.IsEnabled(logLevel);
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => inner.Log(logLevel, eventId, state, exception, formatter);
    }
}

public class LyricsSettingsTests
{
    private const string Token = "lyrics-settings-token-do-not-leak";

    [Fact]
    public async Task Saving_lyrics_settings_is_additive_and_leaves_the_server_token()
    {
        using var fixture = new ServiceFixture();
        var servers = fixture.Get<PlexServerService>();
        var saved = await servers.SaveAsync(null, "Shelf", "http://plex.test:32400", Token);
        var page = new LyricsPageService(
            fixture.Provider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>(),
            fixture.Provider.GetRequiredService<ISecretProtector>(),
            fixture.Get<PlexReadClient>(),
            new PlexLyricsClient(new HandlerTransport(fixture.Handler), new ForwardingPlexWriteTransport(new HandlerTransport(fixture.Handler)), new AllowAllAddressGuard(), NullLogger<PlexLyricsClient>.Instance),
            new StubAdmin("admin"),
            new LyricsJobs(fixture.Provider.GetRequiredService<IServiceScopeFactory>(), NullLogger<LyricsJobs>.Instance));

        var before = await page.LoadAsync();
        var form = Assert.Single(before);
        Assert.False(form.AutoCheck);
        Assert.Empty(form.Maps);

        await page.SaveAsync(new LyricsSettingsInput
        {
            ServerId = saved.Id,
            AutoCheck = true,
            IntervalHours = 12,
            Maps = [new LyricsMapInput { PlexPrefix = "/mnt/user/media/music", ContainerPrefix = "/music" }]
        });
        var enabled = Assert.Single(await page.LoadAsync());
        Assert.True(enabled.AutoCheck);
        Assert.NotNull(enabled.LastAutoCheckUtc);
        var stamp = enabled.LastAutoCheckUtc;
        await page.SaveAsync(new LyricsSettingsInput
        {
            ServerId = saved.Id,
            AutoCheck = true,
            IntervalHours = 6,
            Maps = [new LyricsMapInput { PlexPrefix = "/mnt/user/media/music", ContainerPrefix = "/music" }]
        });
        var again = Assert.Single(await page.LoadAsync());
        Assert.Equal(stamp, again.LastAutoCheckUtc);
        Assert.Equal(6, again.IntervalHours);

        await using var scope = fixture.Provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var server = await db.PlexServers.SingleAsync();
        Assert.Equal("http://plex.test:32400", server.BaseUrl);
        Assert.Equal(1, server.Slot);
        Assert.Equal(Token, scope.ServiceProvider.GetRequiredService<ISecretProtector>().Unprotect(server.ProtectedToken!));
        Assert.NotEqual(Token, server.ProtectedToken);
        fixture.AssertNoSecret(Token);
    }

    private sealed class StubAdmin(string id) : IAdminGate
    {
        public Task<string> RequireUserIdAsync(CancellationToken cancellationToken) => Task.FromResult(id);
    }
}
