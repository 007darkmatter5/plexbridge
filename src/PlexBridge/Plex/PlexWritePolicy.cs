using Microsoft.AspNetCore.WebUtilities;

namespace PlexBridge.Plex;

/// <summary>
/// The only Plex mutations this version sends. The read-only inventory client cannot call these paths.
/// Scrobble, progress, and rating use the endpoints Plex actually implements. Playback, library scans, and deletes are rejected.
/// </summary>
public static partial class PlexWritePolicy
{
    public static bool IsAllowed(HttpMethod method, string relativePathAndQuery)
    {
        if (string.IsNullOrWhiteSpace(relativePathAndQuery))
            return false;
        if (relativePathAndQuery.Contains("X-Plex-Token", StringComparison.OrdinalIgnoreCase)
            || relativePathAndQuery.Contains("token=", StringComparison.OrdinalIgnoreCase))
            return false;

        var queryIndex = relativePathAndQuery.IndexOf('?');
        var path = queryIndex >= 0 ? relativePathAndQuery[..queryIndex] : relativePathAndQuery;
        var rawQuery = queryIndex >= 0 ? relativePathAndQuery[(queryIndex + 1)..] : "";
        if (path.Contains('\\') || path.Contains("..", StringComparison.Ordinal))
            return false;
        if (path.Length > 1)
            path = path.TrimEnd('/');
        var query = QueryHelpers.ParseQuery(rawQuery);

        if (path.Equals("/:/scrobble", StringComparison.OrdinalIgnoreCase) || path.Equals("/:/unscrobble", StringComparison.OrdinalIgnoreCase))
            return method == HttpMethod.Get && Keys(query, "key", "identifier") && RatingKey(query) && LibraryIdentifier(query);

        if (path.Equals("/:/progress", StringComparison.OrdinalIgnoreCase))
            return method == HttpMethod.Get
                && Keys(query, "key", "identifier", "time", "state")
                && RatingKey(query)
                && LibraryIdentifier(query)
                && query.TryGetValue("state", out var state) && state.ToString() == "stopped"
                && long.TryParse(query["time"], out var time) && time is >= 0 and <= 172_800_000;

        if (path.Equals("/:/rate", StringComparison.OrdinalIgnoreCase))
            return method == HttpMethod.Put
                && Keys(query, "key", "identifier", "rating")
                && RatingKey(query)
                && LibraryIdentifier(query)
                && decimal.TryParse(query["rating"], System.Globalization.CultureInfo.InvariantCulture, out var rating)
                && rating is >= -1 and <= 10;

        if (path.Equals("/playlists", StringComparison.OrdinalIgnoreCase))
            return method == HttpMethod.Post
                && Keys(query, "type", "title", "smart", "uri")
                && query["type"].ToString() is "video" or "audio"
                && query["smart"].ToString() == "0"
                && SafeTitle(query["title"].ToString())
                && ItemUri(query["uri"].ToString());

        if (PlaylistItems().IsMatch(path))
            return method == HttpMethod.Put && Keys(query, "uri") && ItemUri(query["uri"].ToString());

        return false;
    }

    public static void EnsureAllowed(HttpMethod method, string relativePathAndQuery)
    {
        if (!IsAllowed(method, relativePathAndQuery))
            throw new PlexRequestException("That Plex change is not one of the migration writes.");
    }

    public static string ItemUriFor(string machineId, string ratingKey)
    {
        if (!IsMachineId(machineId) || !IsDigits(ratingKey))
            throw new PlexRequestException("The playlist target is not valid.");
        return "server://" + machineId + "/com.plexapp.plugins.library/library/metadata/" + ratingKey;
    }

    private static bool ItemUri(string uri)
    {
        const string marker = "/com.plexapp.plugins.library/library/metadata/";
        if (!uri.StartsWith("server://", StringComparison.Ordinal) || !uri.Contains(marker, StringComparison.Ordinal))
            return false;
        var machine = uri["server://".Length..uri.IndexOf(marker, StringComparison.Ordinal)];
        var ratingKey = uri[(uri.IndexOf(marker, StringComparison.Ordinal) + marker.Length)..];
        return IsMachineId(machine) && IsDigits(ratingKey) && !uri.Contains('?');
    }

    private static bool SafeTitle(string title)
        => title.Length is > 0 and <= 200
           && !title.Any(char.IsControl)
           && !title.Contains("token", StringComparison.OrdinalIgnoreCase)
           && !title.Contains("://", StringComparison.Ordinal);

    private static bool LibraryIdentifier(Dictionary<string, Microsoft.Extensions.Primitives.StringValues> query)
        => query.TryGetValue("identifier", out var identifier) && identifier.ToString() == "com.plexapp.plugins.library";

    private static bool RatingKey(Dictionary<string, Microsoft.Extensions.Primitives.StringValues> query)
        => query.TryGetValue("key", out var key) && IsDigits(key.ToString());

    private static bool Keys(Dictionary<string, Microsoft.Extensions.Primitives.StringValues> query, params string[] expected)
    {
        if (query.Count != expected.Length)
            return false;
        foreach (var name in expected)
        {
            if (!query.Keys.Any(key => key.Equals(name, StringComparison.OrdinalIgnoreCase)))
                return false;
        }

        return true;
    }

    private static bool IsDigits(string value)
        => value.Length is > 0 and <= 12 && value.All(char.IsDigit);

    private static bool IsMachineId(string value)
        => value.Length is >= 8 and <= 64 && value.All(char.IsAsciiHexDigit);

    [System.Text.RegularExpressions.GeneratedRegex("^/playlists/[0-9]{1,12}/items$", System.Text.RegularExpressions.RegexOptions.CultureInvariant)]
    private static partial System.Text.RegularExpressions.Regex PlaylistItems();
}
