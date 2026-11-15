using System.Globalization;
using System.Text.Json;

namespace PlexBridge.Watchdog;

public static class PlexSessionParser
{
    public static IReadOnlyList<WatchedSession> Parse(string json, DateTimeOffset at)
    {
        if (string.IsNullOrWhiteSpace(json))
            throw new Plex.PlexRequestException("The session list could not be read.");

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            var container = Property(root, "MediaContainer") ?? root;
            var sessions = new List<WatchedSession>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in Elements(container, "Metadata"))
            {
                var session = ReadSession(item, at);
                if (session is null || !seen.Add(session.PlaySessionId))
                    continue;
                sessions.Add(session);
            }

            return sessions;
        }
        catch (JsonException)
        {
            throw new Plex.PlexRequestException("The session list could not be read.");
        }
    }

    private static WatchedSession? ReadSession(JsonElement item, DateTimeOffset at)
    {
        var playId = Safe(Text(Property(item, "Session"), "id")) ?? Safe(Text(item, "sessionKey"));
        if (playId is null)
            return null;

        var transcode = Property(item, "TranscodeSession");
        var media = Elements(item, "Media").FirstOrDefault();
        var part = media.ValueKind == JsonValueKind.Undefined
            ? default
            : Elements(media, "Part").FirstOrDefault(candidate => Text(candidate, "file") is not null);
        if (part.ValueKind == JsonValueKind.Undefined && media.ValueKind != JsonValueKind.Undefined)
            part = Elements(media, "Part").FirstOrDefault();

        var streams = part.ValueKind == JsonValueKind.Undefined ? [] : Elements(part, "Stream").ToList();
        var subtitle = streams.FirstOrDefault(stream => Int(stream, "streamType") == 3 && (Bool(stream, "selected") == true || Decision(stream) == "burn"));
        var subtitleDecision = Text(transcode, "subtitleDecision") ?? (subtitle.ValueKind == JsonValueKind.Undefined ? null : Decision(subtitle));
        var burn = string.Equals(subtitleDecision, "burn", StringComparison.OrdinalIgnoreCase);
        var videoDecision = Text(transcode, "videoDecision");
        var audioDecision = Text(transcode, "audioDecision");
        var playDecision = Classify(transcode is not null, videoDecision, audioDecision, burn);
        var width = Int(media, "width");
        var height = Int(media, "height");
        var resolution = width is > 0 && height is > 0
            ? width.Value.ToString(CultureInfo.InvariantCulture) + "x" + height.Value.ToString(CultureInfo.InvariantCulture)
            : Text(media, "videoResolution");

        var state = Text(Property(item, "Player"), "state") ?? "paused";
        var offset = Long(item, "viewOffset") ?? 0;
        if (offset < 0)
            offset = 0;
        var speed = Double(transcode, "speed");
        var throttled = OptionalBool(transcode, "throttled");
        var progress = Double(transcode, "progress");
        var hardware = OptionalBool(transcode, "transcodeHwRequested");
        var fullPipeline = OptionalBool(transcode, "transcodeHwFullPipeline");
        var sample = new StreamSample(
            at,
            state,
            offset,
            Long(item, "duration"),
            transcode is not null,
            speed,
            throttled,
            progress,
            videoDecision,
            audioDecision,
            subtitleDecision,
            hardware,
            fullPipeline);

        return new WatchedSession(
            playId,
            Safe(Text(item, "sessionKey")),
            transcode is null ? null : Safe(Text(transcode, "key")),
            Title(item),
            WatchdogText.ClipOrNull(Text(Property(item, "User"), "title"), 200),
            WatchdogText.ClipOrNull(Text(Property(item, "Player"), "product"), 80),
            WatchdogText.ClipOrNull(Text(Property(item, "Player"), "platform"), 80),
            WatchdogText.ClipOrNull(Text(Property(item, "Player"), "device"), 80),
            Guids(item),
            Safe(Text(item, "ratingKey")),
            FilePath(Text(part, "file")),
            Safe(Text(part, "id")),
            WatchdogText.ClipOrNull(Text(media, "container") ?? Text(part, "container"), 40),
            WatchdogText.ClipOrNull(Text(media, "videoCodec") ?? SelectedCodec(streams, 1), 40),
            WatchdogText.ClipOrNull(Text(media, "audioCodec") ?? SelectedCodec(streams, 2), 40),
            WatchdogText.ClipOrNull(resolution, 40),
            Bitrate(Int(media, "bitrate")),
            playDecision,
            hardware,
            fullPipeline,
            burn,
            WatchdogText.ClipOrNull(subtitle.ValueKind == JsonValueKind.Undefined ? null : Text(subtitle, "codec"), 40),
            sample);
    }

    private static string Classify(bool transcodePresent, string? videoDecision, string? audioDecision, bool burn)
    {
        if (!transcodePresent)
            return PlayDecisions.DirectPlay;
        var video = videoDecision?.Trim().ToLowerInvariant();
        var audio = audioDecision?.Trim().ToLowerInvariant();
        var videoCopy = video is "copy" or "directplay";
        var audioCopy = audio is null or "copy" or "directplay";
        if (!burn && videoCopy && audioCopy)
            return PlayDecisions.DirectStream;
        return PlayDecisions.Transcode;
    }

    private static string Title(JsonElement item)
    {
        var title = Text(item, "title") ?? "Untitled";
        var show = Text(item, "grandparentTitle");
        if (string.IsNullOrWhiteSpace(show))
            return WatchdogText.Clip(title, 300);
        var season = Int(item, "parentIndex");
        var episode = Int(item, "index");
        var index = season is > 0 && episode is > 0
            ? "S" + season.Value.ToString("00", CultureInfo.InvariantCulture) + "E" + episode.Value.ToString("00", CultureInfo.InvariantCulture) + " "
            : "";
        return WatchdogText.Clip(show + " — " + index + title, 300);
    }

    private static string? Guids(JsonElement item)
    {
        var ids = new List<string>();
        if (item.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in item.EnumerateObject())
            {
                if (!property.Name.Equals("guid", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (property.Value.ValueKind == JsonValueKind.String)
                    AddGuid(ids, property.Value.GetString());
                else if (property.Value.ValueKind == JsonValueKind.Array)
                {
                    foreach (var entry in property.Value.EnumerateArray())
                        AddGuid(ids, entry.ValueKind == JsonValueKind.String ? entry.GetString() : Text(entry, "id"));
                }
                else if (property.Value.ValueKind == JsonValueKind.Object)
                    AddGuid(ids, Text(property.Value, "id"));
            }
        }

        if (ids.Count == 0)
            return null;
        return WatchdogText.ClipOrNull(string.Join(", ", ids), 500);
    }

    private static void AddGuid(List<string> ids, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return;
        var text = value.Trim();
        if (text.Length > 200 || text.Contains("token", StringComparison.OrdinalIgnoreCase))
            return;
        if (text.StartsWith("file://", StringComparison.OrdinalIgnoreCase) || text.StartsWith("local://", StringComparison.OrdinalIgnoreCase))
            return;
        if (!ids.Contains(text, StringComparer.Ordinal))
            ids.Add(text);
    }

    private static string? FilePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;
        var text = path.Trim();
        var query = text.IndexOf('?');
        if (query >= 0)
            text = text[..query];
        return WatchdogText.ClipOrNull(text, 500);
    }

    private static string? SelectedCodec(IReadOnlyList<JsonElement> streams, int streamType)
    {
        var stream = streams.FirstOrDefault(item => Int(item, "streamType") == streamType && Bool(item, "selected") == true);
        if (stream.ValueKind == JsonValueKind.Undefined)
            stream = streams.FirstOrDefault(item => Int(item, "streamType") == streamType);
        return stream.ValueKind == JsonValueKind.Undefined ? null : Text(stream, "codec");
    }

    private static int? Bitrate(int? value)
    {
        if (value is null or <= 0)
            return null;
        var kbps = value.Value > 1_000_000 ? value.Value / 1000 : value.Value;
        return kbps is > 0 and < 1_000_000 ? kbps : null;
    }

    private static string? Decision(JsonElement element) => Text(element, "decision");

    private static string? Safe(string? value) => PlexWatchdogPolicy.SafeId(value);

    private static JsonElement? Property(JsonElement? parent, string name)
    {
        if (parent is null || parent.Value.ValueKind != JsonValueKind.Object)
            return null;
        foreach (var property in parent.Value.EnumerateObject())
        {
            if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase) && property.Value.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined)
                return property.Value;
        }

        return null;
    }

    private static IEnumerable<JsonElement> Elements(JsonElement parent, string name)
    {
        var value = Property(parent, name);
        if (value is null)
            yield break;
        if (value.Value.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in value.Value.EnumerateArray())
                yield return item;
            yield break;
        }

        if (value.Value.ValueKind == JsonValueKind.Object)
            yield return value.Value;
    }

    private static string? Text(JsonElement? parent, string name)
    {
        var value = Property(parent, name);
        if (value is null)
            return null;
        var text = value.Value.ValueKind switch
        {
            JsonValueKind.String => value.Value.GetString(),
            JsonValueKind.Number => value.Value.GetRawText(),
            _ => null
        };
        return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
    }

    private static bool? Bool(JsonElement parent, string name) => OptionalBool(parent, name);

    private static bool? OptionalBool(JsonElement? parent, string name)
    {
        var value = Property(parent, name);
        if (value is null)
            return null;
        return value.Value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Number when value.Value.TryGetInt32(out var number) => number != 0,
            JsonValueKind.String => value.Value.GetString()?.Trim().ToLowerInvariant() switch
            {
                "1" or "true" => true,
                "0" or "false" => false,
                _ => null
            },
            _ => null
        };
    }

    private static int? Int(JsonElement parent, string name)
    {
        var value = Property(parent, name);
        if (value is null)
            return null;
        if (value.Value.ValueKind == JsonValueKind.Number && value.Value.TryGetInt32(out var number))
            return number;
        if (value.Value.ValueKind == JsonValueKind.String && int.TryParse(value.Value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out number))
            return number;
        return null;
    }

    private static long? Long(JsonElement parent, string name)
    {
        var value = Property(parent, name);
        if (value is null)
            return null;
        if (value.Value.ValueKind == JsonValueKind.Number && value.Value.TryGetInt64(out var number))
            return number;
        if (value.Value.ValueKind == JsonValueKind.String && long.TryParse(value.Value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out number))
            return number;
        return null;
    }

    private static double? Double(JsonElement? parent, string name)
    {
        var value = Property(parent, name);
        if (value is null)
            return null;
        if (value.Value.ValueKind == JsonValueKind.Number && value.Value.TryGetDouble(out var number))
            return number;
        if (value.Value.ValueKind == JsonValueKind.String && double.TryParse(value.Value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out number))
            return number;
        return null;
    }
}
