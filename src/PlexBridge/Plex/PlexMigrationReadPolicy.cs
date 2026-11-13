using Microsoft.AspNetCore.WebUtilities;

namespace PlexBridge.Plex;

/// <summary>
/// GET-only reads used to build a migration plan. Writes, playback, and tokens in the query stay rejected.
/// The inventory client keeps its own narrower allowlist.
/// </summary>
public static partial class PlexMigrationReadPolicy
{
    public static bool IsAllowed(HttpMethod method, string relativePathAndQuery)
    {
        if (method != HttpMethod.Get || string.IsNullOrWhiteSpace(relativePathAndQuery))
            return false;
        if (!TrySplit(relativePathAndQuery, out var path, out var query))
            return false;

        if (path.Equals("/library/sections", StringComparison.OrdinalIgnoreCase))
            return query.Count == 0;
        if (path.Equals("/playlists", StringComparison.OrdinalIgnoreCase))
            return query.Count == 0;

        if (MetadataItem().IsMatch(path))
            return query.Count == 0;

        if (PlaylistItems().IsMatch(path))
            return Only(query, "X-Plex-Container-Start", "X-Plex-Container-Size");

        if (SectionAll().IsMatch(path))
            return Only(query, "X-Plex-Container-Start", "X-Plex-Container-Size", "type", "includeGuids")
                && GuidsFlag(query)
                && (!query.ContainsKey("type") || IsLibraryType(query["type"].ToString()));

        return false;
    }

    public static void EnsureAllowed(HttpMethod method, string relativePathAndQuery)
    {
        if (!IsAllowed(method, relativePathAndQuery))
            throw new PlexRequestException("That Plex read is not part of a migration preview.");
    }

    private static bool IsLibraryType(string value)
        => value is "1" or "2" or "3" or "4" or "8" or "9" or "10";

    private static bool GuidsFlag(Dictionary<string, Microsoft.Extensions.Primitives.StringValues> query)
        => !query.TryGetValue("includeGuids", out var value) || value.ToString() == "1";

    private static bool Only(Dictionary<string, Microsoft.Extensions.Primitives.StringValues> query, params string[] allowed)
    {
        foreach (var key in query.Keys)
        {
            if (!allowed.Any(name => name.Equals(key, StringComparison.OrdinalIgnoreCase)))
                return false;
        }

        return true;
    }

    private static bool TrySplit(string relative, out string path, out Dictionary<string, Microsoft.Extensions.Primitives.StringValues> query)
    {
        query = [];
        path = relative;
        if (relative.Contains('\\') || relative.Contains("..", StringComparison.Ordinal) || relative.Contains("X-Plex-Token", StringComparison.OrdinalIgnoreCase) || relative.Contains("token=", StringComparison.OrdinalIgnoreCase))
            return false;
        var queryIndex = relative.IndexOf('?');
        path = queryIndex >= 0 ? relative[..queryIndex] : relative;
        var rawQuery = queryIndex >= 0 ? relative[(queryIndex + 1)..] : "";
        if (!path.StartsWith('/'))
            path = "/" + path;
        if (path.Length > 1)
            path = path.TrimEnd('/');
        query = QueryHelpers.ParseQuery(rawQuery).ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
        return true;
    }

    [System.Text.RegularExpressions.GeneratedRegex("^/library/sections/[0-9]{1,10}/all$", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant)]
    private static partial System.Text.RegularExpressions.Regex SectionAll();

    [System.Text.RegularExpressions.GeneratedRegex("^/library/metadata/[0-9]{1,12}$", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant)]
    private static partial System.Text.RegularExpressions.Regex MetadataItem();

    [System.Text.RegularExpressions.GeneratedRegex("^/playlists/[0-9]{1,12}/items$", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant)]
    private static partial System.Text.RegularExpressions.Regex PlaylistItems();
}
