using Microsoft.AspNetCore.WebUtilities;

namespace PlexBridge.Libraries;

/// <summary>
/// Reads used to sample a few real files while mapping library folders.
/// Writes, library scans, and tokens in the query are rejected.
/// </summary>
public static partial class LibraryPathPolicy
{
    public static bool IsAllowed(HttpMethod method, string relativePathAndQuery)
    {
        if (method != HttpMethod.Get || !TrySplit(relativePathAndQuery, out var path, out var query))
            return false;
        if (path.Equals("/library/sections", StringComparison.OrdinalIgnoreCase))
            return query.Count == 0;
        if (!SectionAll().IsMatch(path))
            return false;
        if (!query.TryGetValue("type", out var type) || type.ToString() is not ("1" or "4" or "10"))
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

            return false;
        }

        return true;
    }

    public static void Ensure(HttpMethod method, string relativePathAndQuery)
    {
        if (!IsAllowed(method, relativePathAndQuery))
            throw new Plex.PlexRequestException("That Plex read is not part of a library path check.");
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
}
