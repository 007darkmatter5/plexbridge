using System.Text.RegularExpressions;
using Microsoft.AspNetCore.WebUtilities;

namespace PlexBridge.Plex;

/// <summary>
/// Milestone 1 may only GET server identity and library inventory.
/// Refresh, scrobble, rating, progress, playback, and account calls are rejected before any network I/O.
/// </summary>
public static partial class PlexReadOnlyPolicy
{
    public static bool IsAllowed(HttpMethod method, string relativePathAndQuery)
    {
        if (method != HttpMethod.Get || string.IsNullOrWhiteSpace(relativePathAndQuery))
            return false;

        var queryIndex = relativePathAndQuery.IndexOf('?');
        var path = queryIndex >= 0 ? relativePathAndQuery[..queryIndex] : relativePathAndQuery;
        var query = queryIndex >= 0 ? relativePathAndQuery[(queryIndex + 1)..] : "";

        if (path.Contains('\\') || path.Contains("..", StringComparison.Ordinal))
            return false;
        if (!path.StartsWith('/'))
            path = "/" + path;
        if (path.Length > 1)
            path = path.TrimEnd('/');

        if (!IsAllowedQuery(query))
            return false;

        if (path is "/")
            return true;
        if (path.Equals("/identity", StringComparison.OrdinalIgnoreCase))
            return true;
        if (path.Equals("/library/sections", StringComparison.OrdinalIgnoreCase))
            return true;
        return SectionPath().IsMatch(path);
    }

    public static void EnsureAllowed(HttpMethod method, string relativePathAndQuery)
    {
        if (!IsAllowed(method, relativePathAndQuery))
            throw new PlexRequestException("That Plex request is not available. This version only reads server identity and library inventory.");
    }

    private static bool IsAllowedQuery(string query)
    {
        if (string.IsNullOrEmpty(query))
            return true;
        if (query.Contains("X-Plex-Token", StringComparison.OrdinalIgnoreCase)
            || query.Contains("token=", StringComparison.OrdinalIgnoreCase))
            return false;

        var parsed = QueryHelpers.ParseQuery(query);
        foreach (var key in parsed.Keys)
        {
            if (key.Equals("X-Plex-Container-Start", StringComparison.OrdinalIgnoreCase))
                continue;
            if (key.Equals("X-Plex-Container-Size", StringComparison.OrdinalIgnoreCase))
                continue;
            return false;
        }

        return true;
    }

    [GeneratedRegex("^/library/sections/[0-9]{1,10}(/all)?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SectionPath();
}

public sealed class PlexRequestException(string message) : Exception(message);
