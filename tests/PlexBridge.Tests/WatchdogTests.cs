using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PlexBridge.Data;
using PlexBridge.Plex;
using PlexBridge.Security;
using PlexBridge.Services;
using PlexBridge.Watchdog;

namespace PlexBridge.Tests;

public class StallTrackerTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 15, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Playing_with_a_moving_offset_is_not_a_stall()
    {
        var samples = Enumerable.Range(0, 13).Select(step => Sample(step * 5, "playing", step * 5000L)).ToArray();
        var steps = Play(new StallTracker(), samples);
        Assert.DoesNotContain(steps, step => step.Signal == WatchSignal.Opened);
    }

    [Fact]
    public void Frozen_offset_while_playing_opens_an_incident_and_a_later_move_recovers()
    {
        var tracker = new StallTracker();
        var steps = Play(tracker,
            Sample(0, "playing", 1000),
            Sample(5, "playing", 1000),
            Sample(10, "playing", 1000),
            Sample(15, "playing", 1000),
            Sample(20, "playing", 1000));
        Assert.Equal(WatchSignal.None, steps[3].Signal);
        var opened = steps[4];
        Assert.Equal(WatchSignal.Opened, opened.Signal);
        Assert.Equal(StallRules.OffsetFrozen, opened.Rule);
        Assert.True(opened.Timeline.Count >= 5);

        var recovered = tracker.Observe(Sample(25, "playing", 8000), WatchdogThresholds.Default);
        Assert.Equal(WatchSignal.Recovered, recovered.Signal);
        Assert.Equal(StallRules.OffsetFrozen, recovered.Rule);
    }

    [Fact]
    public void Paused_playback_is_not_a_stall()
    {
        var samples = Enumerable.Range(0, 13).Select(step => Sample(step * 5, "paused", 1000)).ToArray();
        Assert.DoesNotContain(Play(new StallTracker(), samples), step => step.Signal == WatchSignal.Opened);
    }

    [Fact]
    public void Pause_time_does_not_count_toward_the_offset_stall()
    {
        var tracker = new StallTracker();
        var before = Play(tracker,
            Sample(0, "playing", 1000),
            Sample(5, "playing", 1000),
            Sample(10, "playing", 1000),
            Sample(15, "paused", 1000),
            Sample(40, "paused", 1000),
            Sample(45, "playing", 1000));
        Assert.DoesNotContain(before, step => step.Signal == WatchSignal.Opened);
        var opened = tracker.Observe(Sample(50, "playing", 1000), WatchdogThresholds.Default);
        Assert.Equal(WatchSignal.Opened, opened.Signal);
        Assert.Equal(StallRules.OffsetFrozen, opened.Rule);
    }

    [Fact]
    public void Throttled_transcode_that_is_ahead_is_not_a_stall()
    {
        var samples = Enumerable.Range(0, 9)
            .Select(step => Sample(step * 5, "playing", step * 5000L, transcode: true, speed: 0.2, throttled: true, progress: 80))
            .ToArray();
        Assert.DoesNotContain(Play(new StallTracker(), samples), step => step.Signal == WatchSignal.Opened);
    }

    [Fact]
    public void Slow_transcode_that_is_already_ahead_is_not_a_stall()
    {
        var samples = Enumerable.Range(0, 9)
            .Select(step => Sample(step * 5, "playing", step * 5000L, transcode: true, speed: 0.2, throttled: false, progress: 90))
            .ToArray();
        Assert.DoesNotContain(Play(new StallTracker(), samples), step => step.Signal == WatchSignal.Opened);
    }

    [Fact]
    public void Unknown_throttle_does_not_count_as_a_slow_transcode()
    {
        var samples = Enumerable.Range(0, 9)
            .Select(step => Sample(step * 5, "playing", step * 5000L, transcode: true, speed: 0.2, throttled: null, progress: 0))
            .ToArray();
        Assert.DoesNotContain(Play(new StallTracker(), samples), step => step.Signal == WatchSignal.Opened);
    }

    [Fact]
    public void Slow_transcode_below_one_times_and_not_ahead_opens()
    {
        var tracker = new StallTracker();
        var steps = Play(tracker,
            Sample(0, "playing", 0, transcode: true, speed: 0.4, throttled: false, progress: 0),
            Sample(5, "playing", 5000, transcode: true, speed: 0.4, throttled: false, progress: 0),
            Sample(10, "playing", 10000, transcode: true, speed: 0.4, throttled: false, progress: 0),
            Sample(15, "playing", 15000, transcode: true, speed: 0.4, throttled: false, progress: 0),
            Sample(20, "playing", 20000, transcode: true, speed: 0.4, throttled: false, progress: 0));
        Assert.Equal(WatchSignal.None, steps[3].Signal);
        Assert.Equal(WatchSignal.Opened, steps[4].Signal);
        Assert.Equal(StallRules.SlowTranscode, steps[4].Rule);
    }

    [Fact]
    public void Buffering_that_recovers_is_not_an_incident()
    {
        var steps = Play(new StallTracker(),
            Sample(0, "buffering", 1000),
            Sample(10, "buffering", 1000),
            Sample(15, "buffering", 1000),
            Sample(18, "playing", 8000));
        Assert.DoesNotContain(steps, step => step.Signal == WatchSignal.Opened);
    }

    [Fact]
    public void Buffering_under_the_long_threshold_is_not_an_offset_stall()
    {
        var steps = Play(new StallTracker(),
            Sample(0, "buffering", 1000),
            Sample(10, "buffering", 1000),
            Sample(20, "buffering", 1000));
        Assert.DoesNotContain(steps, step => step.Signal == WatchSignal.Opened);
    }

    [Fact]
    public void Long_buffering_opens_and_playing_again_recovers()
    {
        var tracker = new StallTracker();
        var steps = Play(tracker,
            Sample(0, "buffering", 1000),
            Sample(10, "buffering", 1000),
            Sample(20, "buffering", 1000),
            Sample(30, "buffering", 1000));
        Assert.Equal(WatchSignal.Opened, steps[3].Signal);
        Assert.Equal(StallRules.LongBuffering, steps[3].Rule);
        var recovered = tracker.Observe(Sample(35, "playing", 9000), WatchdogThresholds.Default);
        Assert.Equal(WatchSignal.Recovered, recovered.Signal);
    }

    [Fact]
    public void Transcode_that_disappears_while_playback_is_stuck_opens()
    {
        var steps = Play(new StallTracker(),
            Sample(0, "playing", 0, transcode: true, speed: 3, throttled: false, progress: 40),
            Sample(5, "playing", 5000, transcode: true, speed: 3, throttled: false, progress: 45),
            Sample(10, "playing", 5000),
            Sample(15, "playing", 5000),
            Sample(25, "playing", 5000));
        Assert.Equal(WatchSignal.None, steps[3].Signal);
        Assert.Equal(WatchSignal.Opened, steps[4].Signal);
        Assert.Equal(StallRules.TranscodeDisappeared, steps[4].Rule);
    }

    [Fact]
    public void Transcode_that_ends_while_the_offset_keeps_moving_is_a_handoff()
    {
        var steps = Play(new StallTracker(),
            Sample(0, "playing", 0, transcode: true, speed: 3, throttled: false, progress: 40),
            Sample(5, "playing", 5000, transcode: true, speed: 3, throttled: false, progress: 45),
            Sample(10, "playing", 5000),
            Sample(15, "playing", 12000),
            Sample(40, "playing", 40000));
        Assert.DoesNotContain(steps, step => step.Signal == WatchSignal.Opened);
    }

    [Fact]
    public void Transcode_that_drops_while_paused_is_not_a_stall()
    {
        var steps = Play(new StallTracker(),
            Sample(0, "playing", 0, transcode: true, speed: 3, throttled: false, progress: 40),
            Sample(5, "playing", 5000, transcode: true, speed: 3, throttled: false, progress: 45),
            Sample(10, "paused", 5000),
            Sample(40, "paused", 5000));
        Assert.DoesNotContain(steps, step => step.Signal == WatchSignal.Opened);
    }

    [Fact]
    public void A_session_that_leaves_the_poll_ends_an_open_incident()
    {
        var watch = new ServerWatch();
        WatchUpdate? opened = null;
        foreach (var sample in new[]
        {
            Sample(0, "playing", 1000),
            Sample(5, "playing", 1000),
            Sample(10, "playing", 1000),
            Sample(15, "playing", 1000),
            Sample(20, "playing", 1000)
        })
        {
            opened = Assert.Single(watch.Apply([Session("play-1", sample)], WatchdogThresholds.Default));
        }

        Assert.NotNull(opened);
        Assert.Equal(WatchSignal.Opened, opened.Signal);
        var ended = Assert.Single(watch.Apply([], WatchdogThresholds.Default));
        Assert.Equal(WatchSignal.Ended, ended.Signal);
        Assert.Equal("play-1", ended.SessionId);
        Assert.Equal(StallRules.OffsetFrozen, ended.Rule);
    }

    private static IReadOnlyList<WatchStep> Play(StallTracker tracker, params StreamSample[] samples)
        => samples.Select(sample => tracker.Observe(sample, WatchdogThresholds.Default)).ToList();

    private static StreamSample Sample(int seconds, string state, long offset, bool transcode = false, double? speed = null, bool? throttled = null, double? progress = null)
        => new(Start.AddSeconds(seconds), state, offset, 100_000, transcode, speed, throttled, progress, transcode ? "transcode" : null, transcode ? "transcode" : null, null, transcode ? true : null, false);

    private static WatchedSession Session(string id, StreamSample sample) => new(
        id, id, null, "Title", "Alex", "Plex for Windows", "Windows", "PC",
        null, "1", "/media/a.mkv", "1", "mkv", "hevc", "aac", "1920x1080", 8000,
        PlayDecisions.DirectPlay, null, null, false, null, sample);
}

public class WatchdogPatternTests
{
    [Fact]
    public void Groups_repeated_incidents_and_suggests_burned_subtitles_and_a_hevc_client()
    {
        var burns = Enumerable.Range(1, 3).Select(id => Incident(id, "/media/Example Show/The Pilot.mkv", "Living Room", "hevc", "1920x1080", true, "pgs", PlayDecisions.Transcode, null)).ToList();
        var client = new[]
        {
            Incident(4, "/media/films/Big.mkv", "Plex for Windows", "hevc", "3840x2160", false, null, PlayDecisions.Transcode, null),
            Incident(5, "/media/films/Bigger.mkv", "Plex for Windows", "h265", "4k", false, null, PlayDecisions.Transcode, null)
        };
        var report = WatchdogPatterns.Build([.. burns, .. client]);

        Assert.Equal(
            "This file stalled 3 times when transcoding with burned-in PGS subtitles; consider creating an Optimized Version or using SRT subtitles.",
            Assert.Single(report.Suggestions, line => line.Contains("burned-in PGS", StringComparison.Ordinal)));
        Assert.Contains("Client Plex for Windows always transcodes HEVC 4K; check its quality setting.", report.Suggestions);
        Assert.Equal(3, report.Groups.Single(group => group.Kind == "File" && group.Key.Contains("The Pilot.mkv", StringComparison.Ordinal)).Count);
        Assert.Contains(report.Groups, group => group.Kind == "Client" && group.Key == "Plex for Windows" && group.Count == 2);
        Assert.Contains(report.Groups, group => group.Kind == "Format" && group.Key == "HEVC · 1920x1080" && group.Count == 3);
        Assert.Contains(report.Groups, group => group.Kind == "Subtitles" && group.Key == "Burned-in PGS" && group.Count == 3);
        Assert.Contains(report.Groups, group => group.Kind == "Playback" && group.Key == "Transcode");
        Assert.Contains(report.Groups, group => group.Kind == "Hardware");
    }

    [Fact]
    public void A_direct_play_of_hevc_4k_stops_the_always_transcodes_suggestion()
    {
        var report = WatchdogPatterns.Build(
        [
            Incident(1, "/a.mkv", "Plex for Windows", "hevc", "3840x2160", false, null, PlayDecisions.Transcode, false),
            Incident(2, "/b.mkv", "Plex for Windows", "hevc", "3840x2160", false, null, PlayDecisions.Transcode, false),
            Incident(3, "/c.mkv", "Plex for Windows", "hevc", "3840x2160", false, null, PlayDecisions.DirectPlay, null)
        ]);
        Assert.DoesNotContain(report.Suggestions, line => line.Contains("always transcodes", StringComparison.Ordinal));
    }

    [Fact]
    public void Hardware_stalls_without_a_software_stall_suggest_an_optimized_version()
    {
        var report = WatchdogPatterns.Build(
        [
            Incident(1, "/media/films/Stuck.mkv", "Shield", "hevc", "3840x2160", false, null, PlayDecisions.Transcode, true),
            Incident(2, "/media/films/Stuck.mkv", "Shield", "hevc", "3840x2160", false, null, PlayDecisions.Transcode, true)
        ]);
        var suggestion = Assert.Single(report.Suggestions, line => line.Contains("hardware", StringComparison.Ordinal));
        Assert.Contains("Optimized Version", suggestion, StringComparison.Ordinal);
        Assert.Equal(2, report.Groups.Single(group => group.Kind == "Hardware" && group.Key == "Hardware").Count);
    }

    [Fact]
    public void One_incident_is_not_a_pattern()
    {
        var report = WatchdogPatterns.Build([Incident(1, "/only.mkv", "Phone", "hevc", "1080p", false, null, PlayDecisions.DirectPlay, null)]);
        Assert.Empty(report.Groups);
        Assert.Empty(report.Suggestions);
    }

    private static PatternIncident Incident(int id, string file, string client, string codec, string resolution, bool burn, string? subtitle, string decision, bool? hardware)
        => new(id, file, client, "Windows", client, codec, resolution, burn, subtitle, decision, hardware, false);
}

public class PlexSessionParserTests
{
    [Fact]
    public void Reads_a_transcode_session_without_keeping_a_token_or_a_player_address()
    {
        var session = Assert.Single(PlexSessionParser.Parse(SessionJson, DateTimeOffset.Parse("2026-11-15T18:00:00Z")));
        Assert.Equal("sess-abc-1", session.PlaySessionId);
        Assert.Equal("15", session.SessionKey);
        Assert.Equal("sess-abc-1", session.TranscodeSessionId);
        Assert.Equal("Alex", session.AccountTitle);
        Assert.Equal("Plex for Windows", session.PlayerProduct);
        Assert.Equal("Windows", session.PlayerPlatform);
        Assert.Equal("Desktop", session.PlayerDevice);
        Assert.Contains("Example Show", session.DisplayTitle, StringComparison.Ordinal);
        Assert.Contains("S01E01", session.DisplayTitle, StringComparison.Ordinal);
        Assert.Contains("plex://episode/5d776b59ad5437001f79c6f8", session.Guids, StringComparison.Ordinal);
        Assert.Contains("imdb://tt1234567", session.Guids, StringComparison.Ordinal);
        Assert.Equal(@"Z:\media\Example Show\Season 1\The Pilot.mkv", session.FilePath);
        Assert.Equal("mkv", session.Container);
        Assert.Equal("hevc", session.VideoCodec);
        Assert.Equal("truehd", session.AudioCodec);
        Assert.Equal("3840x2160", session.VideoResolution);
        Assert.Equal(18000, session.BitrateKbps);
        Assert.Equal(PlayDecisions.Transcode, session.PlayDecision);
        Assert.True(session.HardwareTranscode);
        Assert.False(session.HardwareFullPipeline);
        Assert.True(session.SubtitleBurn);
        Assert.Equal("pgs", session.SubtitleCodec);
        Assert.Equal("playing", session.Sample.State);
        Assert.Equal(120000, session.Sample.ViewOffsetMs);
        Assert.Equal(0.6, session.Sample.Speed);
        Assert.False(session.Sample.Throttled);
        Assert.Equal(4.5, session.Sample.Progress);
        var stored = JsonSerializer.Serialize(session);
        Assert.DoesNotContain("secret-token-value", stored, StringComparison.Ordinal);
        Assert.DoesNotContain("10.0.0.5", stored, StringComparison.Ordinal);
    }

    [Fact]
    public void A_session_without_a_transcode_is_direct_play()
    {
        var json = """{"MediaContainer":{"Metadata":[{"sessionKey":"3","title":"Arrival","viewOffset":10,"Player":{"state":"paused","product":"Plex Web"},"Session":{"id":"direct-1"},"Media":[{"videoCodec":"h264","audioCodec":"aac","container":"mp4","width":1920,"height":800}]}]}}""";
        var session = Assert.Single(PlexSessionParser.Parse(json, DateTimeOffset.UnixEpoch));
        Assert.Equal(PlayDecisions.DirectPlay, session.PlayDecision);
        Assert.Null(session.TranscodeSessionId);
        Assert.Null(session.HardwareTranscode);
        Assert.Null(session.Sample.Speed);
        Assert.Equal("paused", session.Sample.State);
    }

    [Fact]
    public void An_empty_session_list_is_empty_and_garbage_is_rejected()
    {
        Assert.Empty(PlexSessionParser.Parse("""{"MediaContainer":{"size":0}}""", DateTimeOffset.UnixEpoch));
        var error = Assert.Throws<PlexRequestException>(() => PlexSessionParser.Parse("<html>nope</html>", DateTimeOffset.UnixEpoch));
        Assert.DoesNotContain("token", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    private const string SessionJson = """
        {
          "MediaContainer": {
            "size": 1,
            "Metadata": [
              {
                "sessionKey": "15",
                "ratingKey": "4321",
                "guid": "plex://episode/5d776b59ad5437001f79c6f8",
                "type": "episode",
                "title": "The Pilot",
                "grandparentTitle": "Example Show",
                "parentIndex": 1,
                "index": 1,
                "viewOffset": 120000,
                "duration": 3600000,
                "User": { "id": "1", "title": "Alex", "thumb": "https://plex.tv/users/1/avatar?X-Plex-Token=secret-token-value" },
                "Player": { "state": "playing", "product": "Plex for Windows", "platform": "Windows", "device": "Desktop", "title": "Office PC", "address": "10.0.0.5" },
                "Session": { "id": "sess-abc-1" },
                "TranscodeSession": {
                  "key": "/transcode/sessions/sess-abc-1",
                  "throttled": false,
                  "progress": 4.5,
                  "speed": 0.6,
                  "videoDecision": "transcode",
                  "audioDecision": "transcode",
                  "subtitleDecision": "burn",
                  "transcodeHwRequested": true,
                  "transcodeHwFullPipeline": false
                },
                "Media": [
                  {
                    "bitrate": 18000,
                    "width": 3840,
                    "height": 2160,
                    "videoCodec": "hevc",
                    "audioCodec": "truehd",
                    "container": "mkv",
                    "Part": [
                      {
                        "id": 99,
                        "file": "Z:\\media\\Example Show\\Season 1\\The Pilot.mkv",
                        "Stream": [
                          { "streamType": 1, "codec": "hevc", "selected": true },
                          { "streamType": 2, "codec": "truehd", "selected": true },
                          { "streamType": 3, "codec": "pgs", "decision": "burn", "selected": true }
                        ]
                      }
                    ]
                  }
                ],
                "Guid": [ { "id": "imdb://tt1234567" }, { "id": "tvdb://999" } ]
              }
            ]
          }
        }
        """;
}

public class PlexWatchdogPolicyTests
{
    [Theory]
    [InlineData("/status/sessions")]
    [InlineData("/status/sessions/terminate?sessionId=sess-abc-1&reason=Playback%20looked%20stuck.%20Start%20it%20again.")]
    [InlineData("/video/:/transcode/universal/stop?session=sess-abc-1")]
    public void Allows_the_watchdog_calls(string path)
        => Assert.True(PlexWatchdogPolicy.IsAllowed(HttpMethod.Get, path));

    [Theory]
    [InlineData("POST", "/status/sessions")]
    [InlineData("GET", "/status/sessions?X-Plex-Token=abc")]
    [InlineData("GET", "/status/sessions/terminate?sessionId=abc&reason=hi&token=abc")]
    [InlineData("GET", "/status/sessions/../identity")]
    [InlineData("GET", "/video/:/transcode/universal/stop")]
    [InlineData("DELETE", "/status/sessions/terminate?sessionId=abc&reason=hi")]
    [InlineData("GET", "/library/sections")]
    public void Rejects_anything_outside_those_calls(string method, string path)
        => Assert.False(PlexWatchdogPolicy.IsAllowed(new HttpMethod(method), path));

    [Fact]
    public void The_other_allowlists_still_reject_session_calls()
    {
        Assert.False(PlexReadOnlyPolicy.IsAllowed(HttpMethod.Get, "/status/sessions"));
        Assert.False(PlexMigrationReadPolicy.IsAllowed(HttpMethod.Get, "/status/sessions"));
        Assert.False(PlexWritePolicy.IsAllowed(HttpMethod.Get, "/status/sessions/terminate?sessionId=sess-abc-1&reason=hi"));
        Assert.False(PlexWritePolicy.IsAllowed(HttpMethod.Get, "/video/:/transcode/universal/stop?session=sess-abc-1"));
    }
}

public class WatchdogRecoveryTests
{
    [Fact]
    public void Off_proposes_nothing_and_report_only_describes_the_action()
    {
        Assert.Null(RecoveryPlanner.Propose(WatchdogRecovery.Off, true, "sess-abc-1"));
        var both = RecoveryPlanner.Propose(WatchdogRecovery.ReportOnly, true, "sess-abc-1");
        Assert.NotNull(both);
        Assert.Equal(RecoveryPlanner.StopAndTerminate, both.Kind);
        Assert.Contains("Stop the stuck transcode", both.Text, StringComparison.Ordinal);
        var terminate = RecoveryPlanner.Propose(WatchdogRecovery.Auto, false, null);
        Assert.NotNull(terminate);
        Assert.Equal(RecoveryPlanner.TerminateOnly, terminate.Kind);
        Assert.DoesNotContain("token", both.Text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("token", RecoveryPlanner.TerminateReason, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(5, 20, 20, 15, 30, WatchdogRecovery.ReportOnly, "", true)]
    [InlineData(1, 20, 20, 15, 30, WatchdogRecovery.ReportOnly, "", false)]
    [InlineData(30, 20, 20, 15, 30, WatchdogRecovery.ReportOnly, "", false)]
    [InlineData(5, 20, 20, 15, 30, "Later", "", false)]
    [InlineData(5, 20, 20, 15, 30, WatchdogRecovery.ReportOnly, "logs", false)]
    [InlineData(5, 20, 20, 15, 30, WatchdogRecovery.Off, "/plex-logs?X-Plex-Token=abc", false)]
    public void Settings_reject_a_poll_faster_than_the_stall_and_a_token_in_the_log_path(int poll, int offset, int slow, int missing, int buffering, string mode, string folder, bool ok)
    {
        var error = WatchdogSettingsRules.Validate(poll, offset, slow, missing, buffering, mode, folder);
        Assert.Equal(ok, error is null);
    }
}

public class PlexLogTailTests
{
    [Fact]
    public void Attaches_nearby_error_lines_and_drops_the_token()
    {
        var folder = Path.Combine(Path.GetTempPath(), "plexbridge-logs", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(folder);
        try
        {
            const string token = "log-token-do-not-leak-55";
            var at = new DateTimeOffset(2026, 11, 15, 18, 0, 0, TimeSpan.Zero);
            var near = at.ToLocalTime().ToString("MMM dd, yyyy HH:mm:ss.fff", CultureInfo.InvariantCulture);
            var far = at.ToLocalTime().AddHours(2).ToString("MMM dd, yyyy HH:mm:ss.fff", CultureInfo.InvariantCulture);
            File.WriteAllText(Path.Combine(folder, "Plex Media Server.log"),
                near + " [0x1] ERROR - Transcoder failed X-Plex-Token: " + token + "\n" +
                near + " [0x1] INFO - playback is fine\n" +
                far + " [0x1] ERROR - old failure\n");
            File.WriteAllText(Path.Combine(folder, "Plex Transcoder Statistics.log"),
                near + " [0x2] WARN - encoder stalled\n");
            File.WriteAllText(Path.Combine(folder, "Plex Media Scanner.log"),
                near + " [0x3] ERROR - scanner should stay out\n");

            var lines = PlexLogTail.Excerpt(folder, at);
            var text = string.Join("\n", lines);
            Assert.Contains("Transcoder failed", text, StringComparison.Ordinal);
            Assert.Contains("encoder stalled", text, StringComparison.Ordinal);
            Assert.DoesNotContain(token, text, StringComparison.Ordinal);
            Assert.DoesNotContain("playback is fine", text, StringComparison.Ordinal);
            Assert.DoesNotContain("old failure", text, StringComparison.Ordinal);
            Assert.DoesNotContain("scanner should stay out", text, StringComparison.Ordinal);
            Assert.Empty(PlexLogTail.Excerpt(null, at));
            Assert.Empty(PlexLogTail.Excerpt(Path.Combine(folder, "missing"), at));
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }
}

public class PlexNotificationTests
{
    [Theory]
    [InlineData("https://plex.example:32400", "wss")]
    [InlineData("http://100.64.8.2:32400", "ws")]
    public void The_notification_uri_has_no_token(string server, string scheme)
    {
        var uri = PlexNotifications.WebSocketUri(new Uri(server));
        Assert.Equal(scheme, uri.Scheme);
        Assert.Contains("websockets/notifications", uri.AbsolutePath, StringComparison.Ordinal);
        Assert.DoesNotContain("token", uri.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.True(string.IsNullOrEmpty(uri.Query));
    }

    [Theory]
    [InlineData("""{"NotificationContainer":{"type":"playing"}}""", true)]
    [InlineData("""{"NotificationContainer":{"type":"transcodeSession"}}""", true)]
    [InlineData("""{"NotificationContainer":{"type":"timeline"}}""", false)]
    [InlineData("not-json", false)]
    public void Only_playing_and_transcode_events_wake_a_poll(string json, bool wake)
        => Assert.Equal(wake, PlexNotifications.IsStreamEvent(json));
}

public class PlexSessionClientTests
{
    [Fact]
    public async Task Session_calls_keep_the_token_in_the_header()
    {
        const string token = "client-token-do-not-leak-99";
        var handler = new ScriptHandler
        {
            Responder = request => HttpJson.Json("""{"MediaContainer":{"size":0}}""")
        };
        var transport = new HandlerTransport(handler);
        var logs = new SpyLogger();
        var client = new PlexSessionClient(transport, new ForwardingPlexWriteTransport(transport), new AllowAllAddressGuard(), new TypedLogger<PlexSessionClient>(logs));
        var uri = new Uri("http://plex.test:32400");

        Assert.Empty(await client.GetSessionsAsync(uri, token, DateTimeOffset.UnixEpoch, CancellationToken.None));
        await client.TerminateAsync(uri, token, "sess-abc-1", CancellationToken.None);
        await client.StopTranscodeAsync(uri, token, "sess-abc-1", CancellationToken.None);

        Assert.Equal(3, handler.Requests.Count);
        Assert.All(handler.Requests, request =>
        {
            Assert.Equal(token, request.Token);
            Assert.DoesNotContain(token, request.Uri.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain("X-Plex-Token", request.Uri.Query, StringComparison.OrdinalIgnoreCase);
        });
        Assert.Contains(handler.Requests, request => request.Uri.AbsolutePath == "/status/sessions" && request.Uri.Query == "");
        var terminate = handler.Requests.Single(request => request.Uri.AbsolutePath == "/status/sessions/terminate");
        Assert.Contains("sessionId=sess-abc-1", terminate.Uri.Query, StringComparison.Ordinal);
        Assert.Contains("reason=", terminate.Uri.Query, StringComparison.Ordinal);
        Assert.Contains(handler.Requests, request => request.Uri.AbsolutePath == "/video/:/transcode/universal/stop");
        Assert.DoesNotContain(logs.Messages, message => message.Contains(token, StringComparison.Ordinal));
    }

    private sealed class TypedLogger<T>(SpyLogger inner) : ILogger<T>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => inner.BeginScope(state);
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => inner.Log(logLevel, eventId, state, exception, formatter);
    }
}

public class WatchdogLoadTests
{
    [Fact]
    public async Task Load_reads_a_saved_server()
    {
        using var fixture = new ServiceFixture();
        await using var scope = fixture.Provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var protector = scope.ServiceProvider.GetRequiredService<ISecretProtector>();
        db.PlexServers.Add(new PlexServerConnection
        {
            Name = "Desktop",
            BaseUrl = "http://127.0.0.1:9",
            ProtectedToken = protector.Protect("test-token-value-1"),
            Slot = 2,
            Status = ServerStatuses.NotChecked,
            UpdatedUtc = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();
        var page = new WatchdogPageService(
            fixture.Get<IDbContextFactory<ApplicationDbContext>>(),
            new WatchdogLiveBoard(),
            null!);
        var screen = await page.LoadAsync();
        var form = Assert.Single(screen.Settings);
        Assert.Equal(2, form.Slot);
        Assert.Equal("Desktop", form.Name);
        Assert.False(form.Enabled);
        Assert.Equal(WatchdogRecovery.ReportOnly, form.RecoveryMode);
        Assert.Empty(screen.Incidents);
    }
}

public class WatchdogPersistenceTests
{
    [Fact]
    public async Task An_incident_survives_reload_and_a_deleted_server_and_the_token_stays_out()
    {
        const string token = "watchdog-token-do-not-leak-77";
        using var fixture = new ServiceFixture();
        await using var scope = fixture.Provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var protector = scope.ServiceProvider.GetRequiredService<ISecretProtector>();
        var server = new PlexServerConnection
        {
            Name = "Desktop",
            BaseUrl = "http://100.64.8.2:32400",
            ProtectedToken = protector.Protect(token),
            Slot = 2,
            Status = ServerStatuses.Online,
            UpdatedUtc = DateTimeOffset.UtcNow
        };
        db.PlexServers.Add(server);
        await db.SaveChangesAsync();
        db.WatchdogSettings.Add(new WatchdogSetting
        {
            PlexServerConnectionId = server.Id,
            Enabled = true,
            RecoveryMode = WatchdogRecovery.ReportOnly
        });
        db.WatchdogIncidents.Add(new WatchdogIncident
        {
            PlexServerConnectionId = server.Id,
            ServerName = "Desktop",
            ServerSlot = 2,
            PlaySessionId = "sess-abc-1",
            DisplayTitle = "Example Show — S01E01 The Pilot",
            AccountTitle = "Alex",
            StallRule = StallRules.OffsetFrozen,
            Resolution = IncidentResolutions.StillStuck,
            PlayDecision = PlayDecisions.Transcode,
            TimelineJson = "[]",
            ActionStatus = WatchdogActionStatus.Proposed,
            StartedUtc = DateTimeOffset.UtcNow,
            UpdatedUtc = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();

        var again = await db.WatchdogIncidents.AsNoTracking().SingleAsync();
        Assert.Equal(StallRules.OffsetFrozen, again.StallRule);
        Assert.Equal(2, again.ServerSlot);
        Assert.DoesNotContain(token, again.TimelineJson, StringComparison.Ordinal);

        db.PlexServers.Remove(server);
        await db.SaveChangesAsync();
        Assert.Empty(await db.WatchdogSettings.ToListAsync());
        var kept = await db.WatchdogIncidents.AsNoTracking().SingleAsync();
        Assert.Null(kept.PlexServerConnectionId);
        Assert.Equal("Desktop", kept.ServerName);
        fixture.AssertNoSecret(token);
    }
}
