using Microsoft.AspNetCore.WebUtilities;

namespace PlexBridge.Lyrics;

/// <summary>
/// Reads used to list music and to see whether a track already has a lyrics stream.
/// The only write is a metadata refresh of one album or one track. A library scan is rejected.
/// </summary>
public static partial class PlexLyricsPolicy
{
    public static bool IsReadAllowed(HttpMethod method, string relativePathAndQuery)
    {
        if (method != HttpMethod.Get || !TrySplit(relativePathAndQuery, out var path, out var query))
            return false;
        if (path.Equals("/library/sections", StringComparison.OrdinalIgnoreCase))
            return query.Count == 0;
        if (Metadata().IsMatch(path))
            return query.Count == 0;
        if (!SectionAll().IsMatch(path))
            return false;
        if (!query.TryGetValue("type", out var type) || type.ToString() is not ("8" or "9" or "10"))
            return false;

        foreach (var key in query.Keys)
        {
            var value = query[key].ToString();
            if (key.Equals("type", StringComparison.OrdinalIgnoreCase))
                continue;
            if (key.Equals("X-Plex-Container-Start", StringComparison.OrdinalIgnoreCase))
            {
                if (!IsDigits(value, 10))
                    return false;
                continue;
            }

            if (key.Equals("X-Plex-Container-Size", StringComparison.OrdinalIgnoreCase))
            {
                if (!int.TryParse(value, out var size) || size is < 1 or > 100)
                    return false;
                continue;
            }

            if (key.Equals("artist.id", StringComparison.OrdinalIgnoreCase) || key.Equals("album.id", StringComparison.OrdinalIgnoreCase))
            {
                if (!IsDigits(value, 12))
                    return false;
                continue;
            }

            if (key.Equals("sort", StringComparison.OrdinalIgnoreCase))
            {
                if (value != "addedAt:desc")
                    return false;
                continue;
            }

            return false;
        }

        return true;
    }

    public static bool IsWriteAllowed(HttpMethod method, string relativePathAndQuery)
    {
        if (method != HttpMethod.Put || !TrySplit(relativePathAndQuery, out var path, out var query))
            return false;
        return query.Count == 0 && Refresh().IsMatch(path);
    }

    public static void EnsureRead(HttpMethod method, string relativePathAndQuery)
    {
        if (!IsReadAllowed(method, relativePathAndQuery))
            throw new Plex.PlexRequestException("That Plex read is not part of a lyrics lookup.");
    }

    public static void EnsureWrite(HttpMethod method, string relativePathAndQuery)
    {
        if (!IsWriteAllowed(method, relativePathAndQuery))
            throw new Plex.PlexRequestException("That Plex change is not a lyrics refresh.");
    }

    private static bool TrySplit(string relative, out string path, out Dictionary<string, Microsoft.Extensions.Primitives.StringValues> query)
    {
        query = [];
        path = relative;
        if (string.IsNullOrWhiteSpace(relative)
            || relative.Contains('\\')
            || relative.Contains("..", StringComparison.Ordinal)
            || relative.Contains("X-Plex-Token", StringComparison.OrdinalIgnoreCase)
            || relative.Contains("token=", StringComparison.OrdinalIgnoreCase))
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

    private static bool IsDigits(string value, int maxLength)
        => value.Length is > 0 && value.Length <= maxLength && value.All(char.IsDigit);

    [System.Text.RegularExpressions.GeneratedRegex("^/library/sections/[0-9]{1,10}/all$", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant)]
    private static partial System.Text.RegularExpressions.Regex SectionAll();

    [System.Text.RegularExpressions.GeneratedRegex("^/library/metadata/[0-9]{1,12}$", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant)]
    private static partial System.Text.RegularExpressions.Regex Metadata();

    [System.Text.RegularExpressions.GeneratedRegex("^/library/metadata/[0-9]{1,12}/refresh$", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant)]
    private static partial System.Text.RegularExpressions.Regex Refresh();
}
