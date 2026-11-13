using System.Net;
using System.Text.RegularExpressions;

namespace PlexBridge.Plex;

public sealed record PlexUrlNormalization(bool Ok, string Normalized, string Error, string? Notice);

public static partial class PlexServerUrl
{
    public static bool TryNormalize(string? input, out string normalized, out string error)
    {
        var result = Normalize(input);
        normalized = result.Normalized;
        error = result.Error;
        return result.Ok;
    }

    /// <summary>
    /// A hostname with no scheme is treated as https. A URL with no explicit port uses 32400,
    /// because that is the Plex Media Server port. https://host with no port is not rejected;
    /// leaving it on 443 never reaches a normal server.
    /// </summary>
    public static PlexUrlNormalization Normalize(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return Fail("Enter the server URL.");

        var text = input.Trim();
        if (text.Contains("X-Plex-Token", StringComparison.OrdinalIgnoreCase)
            || text.Contains("token=", StringComparison.OrdinalIgnoreCase))
            return Fail("Put the token in the token field, not in the URL.");

        var addedScheme = false;
        if (!text.Contains("://", StringComparison.Ordinal))
        {
            text = "https://" + text.TrimStart('/');
            addedScheme = true;
        }

        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri))
            return Fail("Enter a server address, such as https://host:32400.");

        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            return Fail("Only http and https server URLs are allowed.");

        if (!string.IsNullOrEmpty(uri.UserInfo))
            return Fail("Don't put a username or password in the URL.");

        if (!string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            return Fail("The server URL can't include a query string or fragment.");

        var path = string.IsNullOrEmpty(uri.AbsolutePath) ? "/" : uri.AbsolutePath.TrimEnd('/');
        if (path.Length == 0)
            path = "/";
        var strippedWeb = path.Equals("/web", StringComparison.OrdinalIgnoreCase)
            || path.Equals("/web/index.html", StringComparison.OrdinalIgnoreCase);
        if (!strippedWeb && path != "/")
            return Fail("Use the server root, such as https://host:32400. The Plex Web page path is not the server.");

        if (string.IsNullOrWhiteSpace(uri.IdnHost) || PlexAddressRules.IsBlockedHostName(uri.IdnHost))
            return Fail("That host is not allowed.");

        if (uri.HostNameType is UriHostNameType.IPv4 or UriHostNameType.IPv6)
        {
            if (!IPAddress.TryParse(uri.IdnHost, out var address) || PlexAddressRules.IsBlocked(address))
                return Fail("That address is not allowed.");
        }

        var explicitPort = ExplicitPort().IsMatch(text);
        var port = explicitPort ? uri.Port : 32400;
        var builder = new UriBuilder(uri.Scheme, uri.IdnHost, port);
        var normalized = builder.Uri.GetComponents(UriComponents.SchemeAndServer, UriFormat.SafeUnescaped).TrimEnd('/');
        if (explicitPort && uri.IsDefaultPort && !normalized.EndsWith(":" + port, StringComparison.Ordinal))
            normalized += ":" + port;
        if (normalized.Length == 0)
            return Fail("Enter a server address, such as https://host:32400.");

        var notices = new List<string>();
        if (addedScheme)
            notices.Add("https:// was added.");
        if (!explicitPort)
            notices.Add("No port was entered, so 32400 was used. That is the Plex Media Server port. If a reverse proxy serves this name on 443, change the URL to https://host:443.");
        if (strippedWeb)
            notices.Add("The /web path is the Plex Web app, not the server root, so it was removed.");
        return new PlexUrlNormalization(true, normalized, "", notices.Count == 0 ? null : string.Join(" ", notices));
    }

    private static PlexUrlNormalization Fail(string error) => new(false, "", error, null);

    [GeneratedRegex(@"://[^/\s]+:\d+", RegexOptions.CultureInvariant)]
    private static partial Regex ExplicitPort();

    public static Uri ToUri(string normalized)
    {
        if (!TryNormalize(normalized, out var clean, out var error))
            throw new PlexRequestException(error);
        return new Uri(clean + "/", UriKind.Absolute);
    }
}
