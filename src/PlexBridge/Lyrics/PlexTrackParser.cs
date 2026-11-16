using System.Globalization;
using System.Text.Json;

namespace PlexBridge.Lyrics;

public static class PlexTrackParser
{
    public static PlexCatalogPage Parse(string json)
    {
        var body = Body(json);
        var items = new List<PlexCatalogItem>();
        foreach (var metadata in Elements(body, "Metadata"))
        {
            var ratingKey = Text(metadata, "ratingKey");
            if (string.IsNullOrEmpty(ratingKey))
                continue;
            var (path, lyrics, local, codec) = MediaOf(metadata);
            var durationMs = Long(metadata, "duration") ?? 0;
            items.Add(new PlexCatalogItem(
                ratingKey,
                Text(metadata, "type") ?? "",
                Text(metadata, "title") ?? "Untitled",
                Text(metadata, "originalTitle") ?? Text(metadata, "grandparentTitle") ?? "",
                Text(metadata, "parentTitle") ?? "",
                Text(metadata, "parentRatingKey") ?? "",
                Text(metadata, "grandparentRatingKey") ?? "",
                path,
                DurationSeconds(durationMs),
                Long(metadata, "addedAt"),
                lyrics,
                local,
                codec));
        }

        int? total = Int(body, "totalSize");
        if (total is null)
        {
            var size = Int(body, "size");
            if (size is > 0 && size != items.Count)
                total = size;
        }

        if (total is int known && known < items.Count)
            total = items.Count;
        return new PlexCatalogPage(total, items);
    }

    public static int DurationSeconds(long durationMs)
    {
        if (durationMs <= 0)
            return 0;
        var seconds = (int)Math.Round(durationMs / 1000d, MidpointRounding.AwayFromZero);
        return Math.Clamp(seconds, 0, 24 * 60 * 60);
    }

    private static (string? Path, bool Lyrics, bool Local, string? Codec) MediaOf(JsonElement metadata)
    {
        string? path = null;
        var lyrics = false;
        var local = false;
        string? codec = null;
        foreach (var media in Elements(metadata, "Media"))
        {
            foreach (var part in Elements(media, "Part"))
            {
                path ??= Text(part, "file");
                foreach (var stream in Elements(part, "Stream"))
                {
                    if (!IsLyrics(stream))
                        continue;
                    lyrics = true;
                    var provider = Text(stream, "provider") ?? "";
                    var streamCodec = (Text(stream, "codec") ?? Text(stream, "format") ?? "").ToLowerInvariant();
                    var isLocal = provider.Contains("localmedia", StringComparison.OrdinalIgnoreCase);
                    if (isLocal || codec is null)
                    {
                        local = isLocal;
                        codec = streamCodec.Length == 0 ? null : streamCodec;
                    }
                }
            }
        }

        return (path, lyrics, local, codec);
    }

    private static bool IsLyrics(JsonElement stream)
    {
        if (!stream.TryGetProperty("streamType", out var value) && !TryProperty(stream, "streamType", out value))
            return false;
        return value.ValueKind switch
        {
            JsonValueKind.Number => value.TryGetInt32(out var number) && number == 4,
            JsonValueKind.String => value.GetString() is "4" or "lyrics",
            _ => false
        };
    }

    private static JsonElement Body(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("MediaContainer", out var body)
                && !document.RootElement.TryGetProperty("mediaContainer", out body))
                throw new Plex.PlexRequestException("The server response was not valid Plex data.");
            return body.Clone();
        }
        catch (JsonException)
        {
            throw new Plex.PlexRequestException("The server response was not valid Plex data.");
        }
    }

    private static IEnumerable<JsonElement> Elements(JsonElement parent, string name)
    {
        if (!TryProperty(parent, name, out var value) || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            yield break;
        if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in value.EnumerateArray())
                yield return item;
            yield break;
        }

        if (value.ValueKind == JsonValueKind.Object)
            yield return value;
    }

    private static bool TryProperty(JsonElement parent, string name, out JsonElement value)
    {
        value = default;
        if (parent.ValueKind != JsonValueKind.Object)
            return false;
        foreach (var property in parent.EnumerateObject())
        {
            if (!property.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                continue;
            value = property.Value;
            return true;
        }

        return false;
    }

    private static string? Text(JsonElement parent, string name)
    {
        if (!TryProperty(parent, name, out var value))
            return null;
        var text = value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            _ => null
        };
        return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
    }

    private static int? Int(JsonElement parent, string name)
    {
        var number = Long(parent, name);
        if (number is null or < int.MinValue or > int.MaxValue)
            return null;
        return (int)number;
    }

    private static long? Long(JsonElement parent, string name)
    {
        if (!TryProperty(parent, name, out var value))
            return null;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number))
            return number;
        if (value.ValueKind == JsonValueKind.String && long.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
            return parsed;
        return null;
    }
}
