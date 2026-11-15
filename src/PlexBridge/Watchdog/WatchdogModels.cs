using System.Globalization;
using System.Text.Json;

namespace PlexBridge.Watchdog;

public static class WatchdogDefaults
{
    public const int PollSeconds = 5;
    public const int OffsetStallSeconds = 20;
    public const int SlowTranscodeSeconds = 20;
    public const int TranscodeMissingSeconds = 15;
    public const int LongBufferingSeconds = 30;
    public const double SlowSpeed = 1.0;
    public const double AheadMarginPercent = 2;
    public const int PollMinSeconds = 3;
    public const int PollMaxSeconds = 60;
    public const int ThresholdMinSeconds = 10;
    public const int ThresholdMaxSeconds = 300;
}

public static class WatchdogRecovery
{
    public const string Off = "Off";
    public const string ReportOnly = "ReportOnly";
    public const string Auto = "Auto";

    public static bool IsKnown(string? mode)
        => mode is Off or ReportOnly or Auto;
}

public static class StallRules
{
    public const string OffsetFrozen = "OffsetFrozen";
    public const string SlowTranscode = "SlowTranscode";
    public const string TranscodeDisappeared = "TranscodeDisappeared";
    public const string LongBuffering = "LongBuffering";
}

public static class IncidentResolutions
{
    public const string StillStuck = "StillStuck";
    public const string Recovered = "Recovered";
    public const string UserStopped = "UserStopped";
    public const string EndedByWatchdog = "EndedByWatchdog";
}

public static class PlayDecisions
{
    public const string DirectPlay = "DirectPlay";
    public const string DirectStream = "DirectStream";
    public const string Transcode = "Transcode";
}

public static class WatchdogActionStatus
{
    public const string None = "None";
    public const string Proposed = "Proposed";
    public const string Done = "Done";
    public const string Failed = "Failed";
}

public sealed record WatchdogThresholds(
    TimeSpan OffsetStall,
    TimeSpan SlowTranscode,
    TimeSpan TranscodeMissing,
    TimeSpan LongBuffering)
{
    public static WatchdogThresholds Default { get; } = FromSeconds(
        WatchdogDefaults.OffsetStallSeconds,
        WatchdogDefaults.SlowTranscodeSeconds,
        WatchdogDefaults.TranscodeMissingSeconds,
        WatchdogDefaults.LongBufferingSeconds);

    public static WatchdogThresholds FromSeconds(int offset, int slow, int missing, int buffering)
        => new(
            TimeSpan.FromSeconds(Clamp(offset, WatchdogDefaults.OffsetStallSeconds)),
            TimeSpan.FromSeconds(Clamp(slow, WatchdogDefaults.SlowTranscodeSeconds)),
            TimeSpan.FromSeconds(Clamp(missing, WatchdogDefaults.TranscodeMissingSeconds)),
            TimeSpan.FromSeconds(Clamp(buffering, WatchdogDefaults.LongBufferingSeconds)));

    private static int Clamp(int value, int fallback)
        => value is >= WatchdogDefaults.ThresholdMinSeconds and <= WatchdogDefaults.ThresholdMaxSeconds ? value : fallback;
}

/// <summary>One observation of a play session. Identity fields live on the incident, not on every sample.</summary>
public sealed record StreamSample(
    DateTimeOffset At,
    string State,
    long ViewOffsetMs,
    long? DurationMs,
    bool TranscodePresent,
    double? Speed,
    bool? Throttled,
    double? Progress,
    string? VideoDecision,
    string? AudioDecision,
    string? SubtitleDecision,
    bool? HardwareRequested,
    bool? HardwareFullPipeline);

public enum WatchSignal
{
    None,
    Opened,
    StillOpen,
    Recovered,
    Ended
}

public sealed record WatchStep(
    WatchSignal Signal,
    string? Rule,
    IReadOnlyList<StreamSample> Timeline);

public sealed record WatchedSession(
    string PlaySessionId,
    string? SessionKey,
    string? TranscodeSessionId,
    string DisplayTitle,
    string? AccountTitle,
    string? PlayerProduct,
    string? PlayerPlatform,
    string? PlayerDevice,
    string? Guids,
    string? RatingKey,
    string? FilePath,
    string? PartId,
    string? Container,
    string? VideoCodec,
    string? AudioCodec,
    string? VideoResolution,
    int? BitrateKbps,
    string PlayDecision,
    bool? HardwareTranscode,
    bool? HardwareFullPipeline,
    bool SubtitleBurn,
    string? SubtitleCodec,
    StreamSample Sample);

public sealed record RecoveryPlan(string Kind, string Text);

public static class RecoveryPlanner
{
    public const string StopAndTerminate = "StopAndTerminate";
    public const string TerminateOnly = "Terminate";
    public const string TerminateReason = "Playback looked stuck. Start it again.";

    public static RecoveryPlan? Propose(string? mode, bool transcodeStillRunning, string? transcodeSessionId)
    {
        if (mode is not (WatchdogRecovery.ReportOnly or WatchdogRecovery.Auto))
            return null;

        var stop = transcodeStillRunning && PlexWatchdogPolicy.IsSafeId(transcodeSessionId);
        return stop
            ? new RecoveryPlan(StopAndTerminate, "Stop the stuck transcode, then end the play session so the client can reconnect.")
            : new RecoveryPlan(TerminateOnly, "End the play session so the client can reconnect.");
    }
}

public static class WatchdogSettingsRules
{
    public static string? Validate(int pollSeconds, int offsetSeconds, int slowSeconds, int missingSeconds, int bufferingSeconds, string? mode, string? logFolder)
    {
        if (pollSeconds is < WatchdogDefaults.PollMinSeconds or > WatchdogDefaults.PollMaxSeconds)
            return $"The poll interval has to be between {WatchdogDefaults.PollMinSeconds} and {WatchdogDefaults.PollMaxSeconds} seconds.";
        if (!InRange(offsetSeconds) || !InRange(slowSeconds) || !InRange(missingSeconds) || !InRange(bufferingSeconds))
            return $"Each stall threshold has to be between {WatchdogDefaults.ThresholdMinSeconds} and {WatchdogDefaults.ThresholdMaxSeconds} seconds.";
        if (offsetSeconds < pollSeconds || slowSeconds < pollSeconds || missingSeconds < pollSeconds || bufferingSeconds < pollSeconds)
            return "A stall threshold has to be at least as long as the poll interval, or a single slow poll would look like a stall.";
        if (!WatchdogRecovery.IsKnown(mode))
            return "Recovery is Off, Report-only, or Auto.";
        return NormalizeFolder(logFolder, out _, out var error) ? null : error;
    }

    public static bool NormalizeFolder(string? folder, out string? fullPath, out string? error)
    {
        fullPath = null;
        error = null;
        if (string.IsNullOrWhiteSpace(folder))
            return true;

        var text = folder.Trim();
        if (text.Length > 300)
        {
            error = "The logs folder path is too long.";
            return false;
        }

        if (text.Contains("X-Plex-Token", StringComparison.OrdinalIgnoreCase) || text.Contains('?') || text.Contains("token=", StringComparison.OrdinalIgnoreCase))
        {
            error = "The logs folder is a directory path. It cannot contain a token.";
            return false;
        }

        if (!Path.IsPathRooted(text))
        {
            error = "The logs folder has to be a full path. A mapped volume such as /plex-logs is fine. Leave it blank if the logs are not mounted.";
            return false;
        }

        try
        {
            fullPath = Path.GetFullPath(text);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            error = "The logs folder path could not be read.";
            return false;
        }

        return true;
    }

    private static bool InRange(int seconds)
        => seconds is >= WatchdogDefaults.ThresholdMinSeconds and <= WatchdogDefaults.ThresholdMaxSeconds;
}

public static class WatchdogText
{
    public static string Rule(string? rule) => rule switch
    {
        StallRules.OffsetFrozen => "View offset frozen",
        StallRules.SlowTranscode => "Transcode below 1×",
        StallRules.TranscodeDisappeared => "Transcode disappeared",
        StallRules.LongBuffering => "Long buffering",
        _ => rule ?? ""
    };

    public static string Outcome(string? resolution) => resolution switch
    {
        IncidentResolutions.StillStuck => "Still stuck",
        IncidentResolutions.Recovered => "Recovered on its own",
        IncidentResolutions.UserStopped => "User stopped",
        IncidentResolutions.EndedByWatchdog => "Ended by the watchdog",
        _ => resolution ?? ""
    };

    public static string Decision(string? playDecision, bool? hardware, bool? fullPipeline) => playDecision switch
    {
        PlayDecisions.DirectPlay => "Direct play",
        PlayDecisions.DirectStream => "Direct stream",
        PlayDecisions.Transcode when hardware == true && fullPipeline == true => "Transcode · HW full",
        PlayDecisions.Transcode when hardware == true => "Transcode · HW",
        PlayDecisions.Transcode when hardware == false => "Transcode · SW",
        PlayDecisions.Transcode => "Transcode",
        _ => playDecision ?? "—"
    };

    public static string Client(string? product, string? platform, string? device)
    {
        var main = First(product, platform, device) ?? "Unknown client";
        var extra = !string.IsNullOrWhiteSpace(device) && !main.Contains(device, StringComparison.OrdinalIgnoreCase)
            ? device
            : !string.IsNullOrWhiteSpace(platform) && !main.Contains(platform, StringComparison.OrdinalIgnoreCase) ? platform : null;
        return extra is null ? main : main + " · " + extra;
    }

    public static string Speed(double? speed, bool? throttled)
    {
        if (speed is null)
            return "—";
        var text = speed.Value.ToString("0.0", CultureInfo.InvariantCulture) + "×";
        return throttled == true ? text + " throttled" : text;
    }

    public static string Offset(long milliseconds)
    {
        if (milliseconds < 0)
            milliseconds = 0;
        var time = TimeSpan.FromMilliseconds(milliseconds);
        return time.TotalHours >= 1
            ? $"{(int)time.TotalHours}:{time.Minutes:00}:{time.Seconds:00}"
            : $"{time.Minutes}:{time.Seconds:00}";
    }

    public static string Bitrate(int? kbps)
    {
        if (kbps is null or <= 0)
            return "—";
        return kbps >= 1000
            ? (kbps.Value / 1000.0).ToString("0.0", CultureInfo.InvariantCulture) + " Mbps"
            : kbps.Value.ToString(CultureInfo.InvariantCulture) + " kbps";
    }

    public static string Clip(string? value, int max)
    {
        var text = (value ?? "").Replace('\r', ' ').Replace('\n', ' ').Trim();
        if (text.Length <= max)
            return text;
        return text[..max];
    }

    public static string? ClipOrNull(string? value, int max)
    {
        var text = Clip(value, max);
        return text.Length == 0 ? null : text;
    }

    private static string? First(params string?[] values)
        => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim();
}

public static class TimelineCodec
{
    public const int Cap = 48;
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public static string Serialize(IEnumerable<StreamSample> samples)
    {
        var rows = samples.TakeLast(Cap).Select(TimelineRow.From).ToList();
        return JsonSerializer.Serialize(rows, Options);
    }

    public static string Merge(string? existingJson, IReadOnlyList<StreamSample> latest)
    {
        var rows = Read(existingJson).ToList();
        var last = rows.Count == 0 ? DateTimeOffset.MinValue : rows[^1].At;
        foreach (var sample in latest)
        {
            if (sample.At > last)
                rows.Add(TimelineRow.From(sample));
        }

        if (rows.Count > Cap)
            rows = rows.TakeLast(Cap).ToList();
        return JsonSerializer.Serialize(rows, Options);
    }

    public static IReadOnlyList<TimelineRow> Read(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return [];
        try
        {
            return JsonSerializer.Deserialize<List<TimelineRow>>(json, Options) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    public sealed record TimelineRow(
        DateTimeOffset At,
        string State,
        long OffsetMs,
        double? Speed,
        bool? Throttled,
        double? Progress,
        bool Transcode,
        string? VideoDecision,
        string? AudioDecision,
        string? SubtitleDecision,
        bool? Hw,
        bool? FullHw)
    {
        public static TimelineRow From(StreamSample sample) => new(
            sample.At,
            sample.State,
            sample.ViewOffsetMs,
            sample.Speed,
            sample.Throttled,
            sample.Progress,
            sample.TranscodePresent,
            sample.VideoDecision,
            sample.AudioDecision,
            sample.SubtitleDecision,
            sample.HardwareRequested,
            sample.HardwareFullPipeline);

        public string Describe()
        {
            var speed = Speed is null ? "" : " · " + WatchdogText.Speed(Speed, Throttled);
            var progress = Progress is null ? "" : " · " + Progress.Value.ToString("0", CultureInfo.InvariantCulture) + "%";
            var decision = string.IsNullOrWhiteSpace(VideoDecision) ? "" : " · " + VideoDecision;
            var burn = string.Equals(SubtitleDecision, "burn", StringComparison.OrdinalIgnoreCase) ? " · subtitles burn" : "";
            return $"{At.ToLocalTime():HH:mm:ss} {State} {WatchdogText.Offset(OffsetMs)}{speed}{progress}{decision}{burn}";
        }
    }
}

public static class WatchdogPatterns
{
    public static PatternReport Build(IReadOnlyList<PatternIncident> incidents)
    {
        var groups = new List<PatternGroup>();
        groups.AddRange(Count(incidents.Where(item => !string.IsNullOrWhiteSpace(item.FilePath)), "File", item => FileKey(item.FilePath!)));
        groups.AddRange(Count(incidents, "Client", item => ClientKey(item)));
        groups.AddRange(Count(incidents, "Format", item => FormatKey(item.VideoCodec, item.VideoResolution)));
        groups.AddRange(Count(incidents, "Subtitles", item => item.SubtitleBurn ? "Burned-in " + SubtitleLabel(item.SubtitleCodec) : "Not burned in"));
        groups.AddRange(Count(incidents, "Playback", item => WatchdogText.Decision(item.PlayDecision, null, null)));
        groups.AddRange(Count(incidents, "Hardware", item => HardwareKey(item)));

        return new PatternReport(groups, Suggestions(incidents).ToList());
    }

    public static string FileKey(string path)
        => path.Replace('\\', '/').Trim();

    public static string ClientKey(PatternIncident incident)
        => WatchdogText.Client(incident.PlayerProduct, null, null);

    public static string FormatKey(string? codec, string? resolution)
    {
        var video = NormalizeVideo(codec);
        var size = string.IsNullOrWhiteSpace(resolution) ? "unknown resolution" : resolution.Trim();
        return video + " · " + size;
    }

    private static IEnumerable<PatternGroup> Count(IEnumerable<PatternIncident> incidents, string kind, Func<PatternIncident, string> key)
        => incidents
            .GroupBy(key, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() >= 2 && !string.IsNullOrWhiteSpace(group.Key))
            .OrderByDescending(group => group.Count())
            .ThenBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
            .Select(group => new PatternGroup(kind, group.Key, group.Count()));

    private static IEnumerable<string> Suggestions(IReadOnlyList<PatternIncident> incidents)
    {
        var burns = incidents
            .Where(item => item.SubtitleBurn && item.PlayDecision == PlayDecisions.Transcode && !string.IsNullOrWhiteSpace(item.FilePath))
            .GroupBy(item => FileKey(item.FilePath!), StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() >= 2)
            .OrderByDescending(group => group.Count())
            .ThenBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
            .ToList();
        foreach (var group in burns)
        {
            var codec = SubtitleLabel(group.GroupBy(item => item.SubtitleCodec).OrderByDescending(item => item.Count()).First().Key);
            var subject = burns.Count == 1 ? "This file" : "\"" + Path.GetFileName(group.Key.Replace('\\', '/')) + "\"";
            yield return $"{subject} stalled {group.Count()} times when transcoding with burned-in {codec} subtitles; consider creating an Optimized Version or using SRT subtitles.";
        }

        foreach (var group in incidents.GroupBy(ClientKey, StringComparer.OrdinalIgnoreCase))
        {
            var hevc4k = group.Where(item => IsHevc(item.VideoCodec) && Is4K(item.VideoResolution)).ToList();
            var transcodes = hevc4k.Where(item => item.PlayDecision == PlayDecisions.Transcode).ToList();
            if (transcodes.Count >= 2 && transcodes.Count == hevc4k.Count)
                yield return $"Client {group.Key} always transcodes HEVC 4K; check its quality setting.";
        }

        foreach (var group in incidents.Where(item => !string.IsNullOrWhiteSpace(item.FilePath)).GroupBy(item => FileKey(item.FilePath!), StringComparer.OrdinalIgnoreCase))
        {
            var hardware = group.Count(item => item.PlayDecision == PlayDecisions.Transcode && item.HardwareTranscode == true);
            var software = group.Count(item => item.PlayDecision == PlayDecisions.Transcode && item.HardwareTranscode == false);
            if (hardware >= 2 && software == 0)
            {
                var name = Path.GetFileName(group.Key.Replace('\\', '/'));
                yield return $"\"{name}\" stalled {hardware} times while hardware transcoding. If a software play of the same file is fine, the hardware encoder is the likely lockup; an Optimized Version avoids that transcode.";
            }
        }
    }

    private static string HardwareKey(PatternIncident incident)
    {
        if (incident.PlayDecision != PlayDecisions.Transcode)
            return "No transcode";
        if (incident.HardwareTranscode == true && incident.HardwareFullPipeline == true)
            return "Hardware, full pipeline";
        if (incident.HardwareTranscode == true)
            return "Hardware";
        if (incident.HardwareTranscode == false)
            return "Software";
        return "Transcode, pipeline unknown";
    }

    public static string SubtitleLabel(string? codec)
    {
        if (string.IsNullOrWhiteSpace(codec))
            return "subtitles";
        var value = codec.Trim().ToLowerInvariant();
        if (value is "pgs" or "pgssub" or "hdmv_pgs")
            return "PGS";
        if (value is "vobsub" or "dvd_subtitle" or "dvdsub")
            return "VOBSUB";
        if (value is "srt" or "subrip" or "text")
            return "SRT";
        return codec.Trim().ToUpperInvariant();
    }

    public static string NormalizeVideo(string? codec)
    {
        if (string.IsNullOrWhiteSpace(codec))
            return "Unknown codec";
        var value = codec.Trim().ToLowerInvariant();
        if (value is "hevc" or "h265" or "h.265")
            return "HEVC";
        if (value is "h264" or "avc" or "h.264")
            return "H264";
        return codec.Trim().ToUpperInvariant();
    }

    public static bool IsHevc(string? codec)
        => NormalizeVideo(codec) == "HEVC";

    public static bool Is4K(string? resolution)
    {
        if (string.IsNullOrWhiteSpace(resolution))
            return false;
        var value = resolution.ToLowerInvariant();
        return value.Contains("2160", StringComparison.Ordinal) || value.Contains("3840", StringComparison.Ordinal) || value.Contains("4k", StringComparison.Ordinal);
    }
}

public sealed record PatternIncident(
    int Id,
    string? FilePath,
    string? PlayerProduct,
    string? PlayerPlatform,
    string? PlayerDevice,
    string? VideoCodec,
    string? VideoResolution,
    bool SubtitleBurn,
    string? SubtitleCodec,
    string? PlayDecision,
    bool? HardwareTranscode,
    bool? HardwareFullPipeline);

public sealed record PatternGroup(string Kind, string Key, int Count);

public sealed record PatternReport(IReadOnlyList<PatternGroup> Groups, IReadOnlyList<string> Suggestions);
