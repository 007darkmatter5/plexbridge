using System.Text.Json;

namespace PlexBridge.Plex;

public static class PlexJsonParser
{
    public static PlexIdentityInfo ParseIdentity(string json)
    {
        var body = Body(json);
        return new PlexIdentityInfo(
            Text(body, "machineIdentifier"),
            Text(body, "version"),
            Text(body, "friendlyName"));
    }

    public static IReadOnlyList<PlexSectionInfo> ParseSections(string json)
    {
        var sections = new List<PlexSectionInfo>();
        foreach (var directory in Elements(Body(json), "Directory"))
        {
            var key = Text(directory, "key") ?? "";
            if (key.Length is 0 or > 10 || !key.All(char.IsDigit))
                continue;

            var locations = Elements(directory, "Location")
                .Select(location => Text(location, "path"))
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Select(path => path!)
                .Distinct(StringComparer.Ordinal)
                .ToList();

            sections.Add(new PlexSectionInfo(
                key,
                Text(directory, "title") ?? "Untitled",
                Text(directory, "type") ?? "unknown",
                locations));
        }

        return sections;
    }

    public static PlexItemPage ParseItems(string json)
    {
        var body = Body(json);
        var items = new List<PlexLibraryItem>();
        foreach (var metadata in Elements(body, "Metadata"))
        {
            var (imdb, tmdb, tvdb) = ExternalIds.Read(Elements(metadata, "Guid").Select(guid => Text(guid, "id")));
            items.Add(new PlexLibraryItem(
                Text(metadata, "ratingKey") ?? "",
                Text(metadata, "title") ?? "Untitled",
                Int(metadata, "year"),
                Text(metadata, "type") ?? "",
                imdb,
                tmdb,
                tvdb));
        }

        var total = Int(body, "totalSize") ?? Int(body, "size") ?? items.Count;
        if (total < items.Count)
            total = items.Count;
        return new PlexItemPage(Int(body, "offset") ?? 0, total, items);
    }

    public static MigrationCatalog ParseMigrationPage(string json, bool playlist)
    {
        var body = Body(json);
        if (!playlist)
            return new MigrationCatalog(ParseMigrationItems(body), []);

        var lists = new List<MigrationPlaylist>();
        foreach (var metadata in Elements(body, "Metadata"))
        {
            var ratingKey = Text(metadata, "ratingKey");
            if (string.IsNullOrEmpty(ratingKey))
                continue;
            var type = Text(metadata, "playlistType") ?? Text(metadata, "type") ?? "";
            if (type == "playlist")
                type = "video";
            lists.Add(new MigrationPlaylist(
                ratingKey,
                Text(metadata, "title") ?? "Untitled",
                type,
                Bool(metadata, "smart"),
                ParseMigrationItems(metadata)));
        }

        return new MigrationCatalog([], lists);
    }

    public static IReadOnlyList<MigrationItem> ParseMigrationItems(string json)
        => ParseMigrationItems(Body(json));

    private static List<MigrationItem> ParseMigrationItems(JsonElement parent)
    {
        var items = new List<MigrationItem>();
        foreach (var metadata in Elements(parent, "Metadata"))
        {
            var ratingKey = Text(metadata, "ratingKey");
            if (string.IsNullOrEmpty(ratingKey))
                continue;
            var guids = PlexGuids.Parse(GuidValues(metadata, "guid"));
            var type = Text(metadata, "type") ?? "";
            var index = Int(metadata, "index");
            var parentIndex = Int(metadata, "parentIndex");
            int? season = type switch
            {
                "episode" => parentIndex,
                "season" => index,
                _ => null
            };
            int? number = type is "episode" or "track" ? index : null;
            if (type == "episode" && (season is null || number is null))
            {
                foreach (var guid in guids)
                {
                    if (!PlexGuids.TryLegacyParts(guid, out _, out var legacySeason, out var legacyEpisode))
                        continue;
                    season ??= legacySeason;
                    number ??= legacyEpisode;
                    break;
                }
            }

            items.Add(new MigrationItem(
                ratingKey,
                Text(metadata, "title") ?? "Untitled",
                type,
                Int(metadata, "year"),
                season,
                number,
                Text(metadata, "grandparentTitle"),
                Int(metadata, "viewCount") ?? 0,
                Long(metadata, "viewOffset") ?? 0,
                Decimal(metadata, "userRating"),
                guids,
                Text(metadata, "parentRatingKey"),
                Text(metadata, "grandparentRatingKey"),
                ShowIds(metadata, guids),
                PlexGuids.Parse(GuidValues(metadata, "parentGuid")),
                Text(metadata, "parentTitle"),
                Long(metadata, "duration") ?? 0,
                null,
                FileNameOf(metadata)));
        }

        return items;
    }

    private static IReadOnlyList<PlexContentId> ShowIds(JsonElement metadata, IReadOnlyList<PlexContentId> guids)
    {
        var showIds = PlexGuids.Parse(GuidValues(metadata, "grandparentGuid")).ToList();
        foreach (var guid in guids)
        {
            if (PlexGuids.TryLegacyParts(guid, out var showId, out _, out _) && showIds.TrueForAll(existing => existing.Key != showId.Key))
                showIds.Add(showId);
        }

        return showIds;
    }

    private static string? FileNameOf(JsonElement metadata)
    {
        if (!metadata.TryGetProperty("Media", out var media) || media.ValueKind != JsonValueKind.Array)
            return null;
        foreach (var entry in media.EnumerateArray())
        {
            if (!entry.TryGetProperty("Part", out var parts) || parts.ValueKind != JsonValueKind.Array)
                continue;
            foreach (var part in parts.EnumerateArray())
            {
                var file = Text(part, "file");
                if (string.IsNullOrWhiteSpace(file))
                    continue;
                var name = file.Replace('\\', '/');
                var slash = name.LastIndexOf('/');
                return slash >= 0 ? name[(slash + 1)..] : name;
            }
        }

        return null;
    }

    private static JsonElement Body(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("MediaContainer", out var body)
                && !document.RootElement.TryGetProperty("mediaContainer", out body))
                throw new PlexRequestException("The server response was not valid Plex data.");
            return body.Clone();
        }
        catch (JsonException)
        {
            throw new PlexRequestException("The server response was not valid Plex data.");
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

    private static IEnumerable<string?> GuidValues(JsonElement parent, string name)
    {
        if (parent.ValueKind != JsonValueKind.Object)
            yield break;
        foreach (var property in parent.EnumerateObject())
        {
            if (!property.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                continue;
            var value = property.Value;
            if (value.ValueKind == JsonValueKind.String)
            {
                yield return value.GetString();
                continue;
            }

            if (value.ValueKind != JsonValueKind.Array)
                continue;
            foreach (var item in value.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String)
                    yield return item.GetString();
                else if (item.ValueKind == JsonValueKind.Object)
                    yield return Text(item, "id");
            }
        }
    }

    private static bool Bool(JsonElement parent, string name)
    {
        if (!TryProperty(parent, name, out var value))
            return false;
        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Number => value.TryGetInt32(out var number) && number != 0,
            JsonValueKind.String => value.GetString() is "1" or "true",
            _ => false
        };
    }

    private static long? Long(JsonElement parent, string name)
    {
        if (!TryProperty(parent, name, out var value))
            return null;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number))
            return number;
        if (value.ValueKind == JsonValueKind.String && long.TryParse(value.GetString(), out number))
            return number;
        return null;
    }

    private static decimal? Decimal(JsonElement parent, string name)
    {
        if (!TryProperty(parent, name, out var value))
            return null;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out var number))
            return number;
        if (value.ValueKind == JsonValueKind.String && decimal.TryParse(value.GetString(), System.Globalization.CultureInfo.InvariantCulture, out number))
            return number;
        return null;
    }

    private static int? Int(JsonElement parent, string name)
    {
        if (!TryProperty(parent, name, out var value))
            return null;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number))
            return number;
        if (value.ValueKind == JsonValueKind.String && int.TryParse(value.GetString(), out number))
            return number;
        return null;
    }

    private static bool TryProperty(JsonElement parent, string name, out JsonElement value)
    {
        if (parent.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in parent.EnumerateObject())
            {
                if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }
            }
        }

        value = default;
        return false;
    }
}

public static class ExternalIds
{
    public static (string? Imdb, string? Tmdb, string? Tvdb) Read(IEnumerable<string?> ids)
    {
        string? imdb = null, tmdb = null, tvdb = null;
        foreach (var raw in ids)
        {
            if (string.IsNullOrWhiteSpace(raw))
                continue;
            var original = raw.Trim();
            var lower = original.ToLowerInvariant();
            imdb ??= Slice(original, lower, "imdb://");
            tmdb ??= Slice(original, lower, "themoviedb://") ?? Slice(original, lower, "tmdb://");
            tvdb ??= Slice(original, lower, "thetvdb://") ?? Slice(original, lower, "tvdb://");
        }

        return (imdb, tmdb, tvdb);
    }

    private static string? Slice(string original, string lower, string marker)
    {
        var index = lower.IndexOf(marker, StringComparison.Ordinal);
        if (index < 0)
            return null;
        var value = original[(index + marker.Length)..].Trim();
        if (value.Length == 0 || value.Contains('/') || value.Contains('?'))
            return null;
        return value;
    }
}
