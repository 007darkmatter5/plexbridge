using Microsoft.AspNetCore.WebUtilities;

namespace PlexBridge.Watchdog;

/// <summary>
/// The only Plex calls the watchdog may send. Inventory reads and migration writes keep their own allowlists.
/// The token is never accepted on the query string.
/// </summary>
public static class PlexWatchdogPolicy
{
    public static bool IsAllowed(HttpMethod method, string relativePathAndQuery)
    {
        if (method != HttpMethod.Get || string.IsNullOrWhiteSpace(relativePathAndQuery))
            return false;
        if (!TrySplit(relativePathAndQuery, out var path, out var query))
            return false;

        if (path.Equals("/status/sessions", StringComparison.OrdinalIgnoreCase))
            return query.Count == 0;

        if (path.Equals("/status/sessions/terminate", StringComparison.OrdinalIgnoreCase))
            return Keys(query, "sessionId", "reason")
                && IsSafeId(query["sessionId"].ToString())
                && IsSafeReason(query["reason"].ToString());

        if (path.Equals("/video/:/transcode/universal/stop", StringComparison.OrdinalIgnoreCase))
            return Keys(query, "session") && IsSafeId(query["session"].ToString());

        return false;
    }

    public static void EnsureAllowed(HttpMethod method, string relativePathAndQuery)
    {
        if (!IsAllowed(method, relativePathAndQuery))
            throw new Plex.PlexRequestException("That Plex request is not one of the watchdog calls.");
    }

    public static bool IsSafeId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;
        var text = value.Trim();
        var slash = text.LastIndexOf('/');
        if (slash >= 0)
            text = text[(slash + 1)..];
        if (text.Length is < 1 or > 128)
            return false;
        if (text.Contains("token", StringComparison.OrdinalIgnoreCase))
            return false;
        foreach (var character in text)
        {
            if (!(char.IsAsciiLetterOrDigit(character) || character is '-' or '_'))
                return false;
        }

        return true;
    }

    public static string? SafeId(string? value)
        => IsSafeId(value) ? LastSegment(value!.Trim()) : null;

    private static string LastSegment(string text)
    {
        var slash = text.LastIndexOf('/');
        return slash >= 0 ? text[(slash + 1)..] : text;
    }

    private static bool IsSafeReason(string reason)
        => reason.Length is >= 1 and <= 160
           && !reason.Any(char.IsControl)
           && !reason.Contains("token", StringComparison.OrdinalIgnoreCase)
           && !reason.Contains("://", StringComparison.Ordinal);

    private static bool Keys(Dictionary<string, Microsoft.Extensions.Primitives.StringValues> query, params string[] expected)
    {
        if (query.Count != expected.Length)
            return false;
        foreach (var name in expected)
        {
            if (!query.ContainsKey(name))
                return false;
        }

        return true;
    }

    private static bool TrySplit(string relative, out string path, out Dictionary<string, Microsoft.Extensions.Primitives.StringValues> query)
    {
        query = [];
        path = relative;
        if (relative.Contains('\\')
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
}
