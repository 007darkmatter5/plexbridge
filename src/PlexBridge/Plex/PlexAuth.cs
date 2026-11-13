using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using PlexBridge.Security;

namespace PlexBridge.Plex;

/// <summary>
/// plex.tv account calls only. This is the PIN link flow published by Plex staff:
/// POST /api/v2/pins, open https://app.plex.tv/auth#?... , then GET /api/v2/pins/{id}.
/// The account token is never placed in a URL.
/// </summary>
public static partial class PlexAuthPolicy
{
    public static bool IsAccountHost(Uri uri)
    {
        var host = uri.IdnHost;
        return host.Equals("plex.tv", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".plex.tv", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsAllowed(HttpMethod method, Uri uri)
    {
        if (!uri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase))
            return false;
        if (!uri.IdnHost.Equals("plex.tv", StringComparison.OrdinalIgnoreCase))
            return false;
        if (!string.IsNullOrEmpty(uri.UserInfo))
            return false;
        if (uri.Query.Contains("X-Plex-Token", StringComparison.OrdinalIgnoreCase)
            || uri.Query.Contains("token=", StringComparison.OrdinalIgnoreCase)
            || uri.Query.Contains("authToken", StringComparison.OrdinalIgnoreCase))
            return false;

        var path = uri.AbsolutePath.TrimEnd('/');
        if (path.Length == 0)
            path = "/";
        if (method == HttpMethod.Post && path.Equals("/api/v2/pins", StringComparison.OrdinalIgnoreCase))
            return AllowedQuery(uri.Query, "strong");
        if (method == HttpMethod.Get && path.Equals("/api/v2/user", StringComparison.OrdinalIgnoreCase))
            return string.IsNullOrEmpty(uri.Query);
        if (method == HttpMethod.Get && path.Equals("/api/v2/resources", StringComparison.OrdinalIgnoreCase))
            return AllowedQuery(uri.Query, "includeHttps", "includeRelay");
        if (method == HttpMethod.Get && path.Equals("/api/v2/home/users", StringComparison.OrdinalIgnoreCase))
            return string.IsNullOrEmpty(uri.Query);
        if (method == HttpMethod.Post && HomeSwitch().IsMatch(path))
            return string.IsNullOrEmpty(uri.Query);
        if (method == HttpMethod.Get && PinPath().IsMatch(path))
            return string.IsNullOrEmpty(uri.Query);
        return false;
    }

    private static bool AllowedQuery(string query, params string[] keys)
    {
        if (string.IsNullOrEmpty(query) || query == "?")
            return true;
        var parsed = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(query.TrimStart('?'));
        foreach (var key in parsed.Keys)
        {
            if (!keys.Contains(key, StringComparer.OrdinalIgnoreCase))
                return false;
        }
        return true;
    }

    [System.Text.RegularExpressions.GeneratedRegex("^/api/v2/pins/[0-9]{1,20}$", System.Text.RegularExpressions.RegexOptions.CultureInvariant)]
    private static partial System.Text.RegularExpressions.Regex PinPath();

    [System.Text.RegularExpressions.GeneratedRegex("^/api/v2/home/users/[0-9a-fA-F-]{16,40}/switch$", System.Text.RegularExpressions.RegexOptions.CultureInvariant)]
    private static partial System.Text.RegularExpressions.Regex HomeSwitch();
}

public static class PlexAuthorizeUrl
{
    public static string Build(string clientIdentifier, string code)
    {
        var query = string.Join("&", new[]
        {
            "clientID=" + Uri.EscapeDataString(clientIdentifier),
            "code=" + Uri.EscapeDataString(code),
            "context%5Bdevice%5D%5Bproduct%5D=" + Uri.EscapeDataString("PlexBridge")
        });
        return "https://app.plex.tv/auth#?" + query;
    }
}

public sealed record PlexCreatedPin(long Id, string Code, DateTimeOffset ExpiresUtc);

public sealed record PlexPinStatus(bool Claimed, string? AccountToken);

public sealed record PlexAccountProfile(string? Username, string? PlexUserId);

public sealed record PlexHomeUser(string Uuid, string Name, bool Admin, bool Protected);

public sealed record PlexConnectionCandidate(string Uri, bool Local, bool Relay, bool Https);

public sealed record OwnedPlexServer(
    string Name,
    string ClientIdentifier,
    string? Version,
    string AccessToken,
    bool HttpsRequired,
    IReadOnlyList<PlexConnectionCandidate> Connections);

public sealed record DiscoveredServerView(
    string Name,
    string ClientIdentifier,
    string? Version,
    IReadOnlyList<PlexConnectionCandidate> Connections);

public static class PlexAuthJson
{
    public static PlexCreatedPin ParseCreatedPin(string json, DateTimeOffset now)
    {
        using var document = Parse(json);
        var root = document.RootElement;
        var id = ReadLong(root, "id") ?? throw new PlexRequestException("Plex did not return a PIN.");
        var code = ReadString(root, "code");
        if (string.IsNullOrWhiteSpace(code))
            throw new PlexRequestException("Plex did not return a PIN.");
        var expires = ReadDate(root, "expiresAt") ?? now.AddSeconds(Math.Clamp(ReadLong(root, "expiresIn") ?? 900, 30, 1800));
        if (expires > now.AddMinutes(30))
            expires = now.AddMinutes(30);
        return new PlexCreatedPin(id, code, expires);
    }

    public static PlexPinStatus ParsePinStatus(string json)
    {
        using var document = Parse(json);
        var token = ReadString(document.RootElement, "authToken");
        return new PlexPinStatus(!string.IsNullOrWhiteSpace(token), string.IsNullOrWhiteSpace(token) ? null : token);
    }

    public static IReadOnlyList<PlexHomeUser> ParseHomeUsers(string json)
    {
        using var document = Parse(json);
        var root = document.RootElement;
        JsonElement users;
        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("users", out var nested))
            users = nested;
        else if (root.ValueKind == JsonValueKind.Array)
            users = root;
        else
            return [];
        if (users.ValueKind != JsonValueKind.Array)
            return [];
        var list = new List<PlexHomeUser>();
        foreach (var user in users.EnumerateArray())
        {
            var uuid = ReadString(user, "uuid") ?? ReadString(user, "id");
            if (string.IsNullOrWhiteSpace(uuid))
                continue;
            var name = ReadString(user, "title") ?? ReadString(user, "username") ?? ReadString(user, "friendlyName") ?? "Plex user";
            list.Add(new PlexHomeUser(uuid, name, Flag(user, "admin"), Flag(user, "protected")));
        }

        return list;
    }

    public static string ParseSwitchToken(string json)
    {
        using var document = Parse(json);
        var root = document.RootElement;
        var token = ReadString(root, "authToken");
        if (string.IsNullOrWhiteSpace(token) && root.TryGetProperty("user", out var user))
            token = ReadString(user, "authToken");
        if (string.IsNullOrWhiteSpace(token) || !PlexTokens.IsPlausible(token))
            throw new PlexRequestException("Plex did not return a user token.");
        return token;
    }

    public static IReadOnlyDictionary<string, string> ParseAccessTokens(string json)
    {
        using var document = Parse(json);
        var tokens = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var device in Devices(document.RootElement))
        {
            var provides = ReadString(device, "provides") ?? "";
            if (!provides.Contains("server", StringComparison.OrdinalIgnoreCase))
                continue;
            var id = ReadString(device, "clientIdentifier");
            var token = ReadString(device, "accessToken");
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(token) || !PlexTokens.IsPlausible(token))
                continue;
            tokens[id] = token;
        }

        return tokens;
    }

    public static PlexAccountProfile ParseUser(string json)
    {
        using var document = Parse(json);
        var root = document.RootElement;
        return new PlexAccountProfile(ReadString(root, "username") ?? ReadString(root, "title"), ReadString(root, "id") ?? ReadString(root, "uuid"));
    }

    public static IReadOnlyList<OwnedPlexServer> ParseOwnedServers(string json)
    {
        using var document = Parse(json);
        var devices = Devices(document.RootElement);
        var servers = new List<OwnedPlexServer>();
        foreach (var device in devices)
        {
            if (!Owned(device))
                continue;
            var provides = ReadString(device, "provides") ?? "";
            if (!provides.Contains("server", StringComparison.OrdinalIgnoreCase))
                continue;
            var token = ReadString(device, "accessToken");
            var id = ReadString(device, "clientIdentifier");
            if (string.IsNullOrWhiteSpace(token) || string.IsNullOrWhiteSpace(id))
                continue;
            var connections = new List<PlexConnectionCandidate>();
            foreach (var connection in Elements(device, "connections"))
            {
                var uri = ReadString(connection, "uri");
                if (string.IsNullOrWhiteSpace(uri))
                    continue;
                var protocol = ReadString(connection, "protocol") ?? "";
                var https = protocol.Equals("https", StringComparison.OrdinalIgnoreCase) || uri.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
                connections.Add(new PlexConnectionCandidate(uri, Flag(connection, "local"), Flag(connection, "relay"), https));
            }
            servers.Add(new OwnedPlexServer(
                ReadString(device, "name") ?? "Plex server",
                id,
                ReadString(device, "productVersion"),
                token,
                Flag(device, "httpsRequired"),
                connections));
        }
        return servers;
    }

    public static IReadOnlyList<DiscoveredServerView> WithoutSecrets(IEnumerable<OwnedPlexServer> servers)
        => servers.Select(server => new DiscoveredServerView(server.Name, server.ClientIdentifier, server.Version, server.Connections)).ToList();

    private static JsonDocument Parse(string json)
    {
        try
        {
            return JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            throw new PlexRequestException("Plex did not return usable account data.");
        }
    }

    private static IEnumerable<JsonElement> Devices(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Array)
            return root.EnumerateArray().ToList();
        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("MediaContainer", out var container))
            return Elements(container, "Device").ToList();
        return [];
    }

    private static IEnumerable<JsonElement> Elements(JsonElement parent, string name)
    {
        if (parent.ValueKind != JsonValueKind.Object)
            yield break;
        foreach (var property in parent.EnumerateObject())
        {
            if (!property.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                continue;
            if (property.Value.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in property.Value.EnumerateArray())
                    yield return item;
            }
            else if (property.Value.ValueKind == JsonValueKind.Object)
            {
                yield return property.Value;
            }
        }
    }

    private static bool Owned(JsonElement device)
    {
        if (!Try(device, "owned", out var value))
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

    private static bool Flag(JsonElement element, string name)
    {
        if (!Try(element, name, out var value))
            return false;
        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.Number => value.TryGetInt32(out var number) && number != 0,
            JsonValueKind.String => value.GetString() is "1" or "true",
            _ => false
        };
    }

    private static string? ReadString(JsonElement element, string name)
    {
        if (!Try(element, name, out var value))
            return null;
        var text = value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            _ => null
        };
        return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
    }

    private static long? ReadLong(JsonElement element, string name)
    {
        if (!Try(element, name, out var value))
            return null;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number))
            return number;
        if (value.ValueKind == JsonValueKind.String && long.TryParse(value.GetString(), out number))
            return number;
        return null;
    }

    private static DateTimeOffset? ReadDate(JsonElement element, string name)
    {
        var text = ReadString(element, name);
        return DateTimeOffset.TryParse(text, out var parsed) ? parsed : null;
    }

    private static bool Try(JsonElement element, string name, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
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

public static class PlexConnectionOrder
{
    /// <summary>
    /// How many addresses one Check connections pass tests by itself.
    /// Every address still stays on screen. A normal server list is smaller than this.
    /// </summary>
    public const int AutomaticBatchSize = 16;

    public static IReadOnlyList<PlexConnectionCandidate> Order(IEnumerable<PlexConnectionCandidate> connections)
    {
        return connections
            .OrderBy(connection => connection.Https ? 0 : 1)
            .ThenBy(connection => connection.Relay ? 1 : 0)
            .ThenBy(connection => PrefersDirectLan(connection.Uri) ? 0 : 1)
            .ToList();
    }

    /// <summary>10/8, 192.168/16, and Tailscale 100.64/10 sort first. Other addresses, including 172.16/12, stay in the list.</summary>
    public static bool PrefersDirectLan(string uri)
    {
        if (!Uri.TryCreate(uri, UriKind.Absolute, out var parsed))
            return false;
        var ip = AddressFromHost(parsed.IdnHost);
        if (ip is null || ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
            return false;
        var bytes = ip.GetAddressBytes();
        if (bytes[0] == 10)
            return true;
        if (bytes[0] == 192 && bytes[1] == 168)
            return true;
        if (bytes[0] == 100 && bytes[1] >= 64 && bytes[1] <= 127)
            return true;
        return false;
    }

    private static System.Net.IPAddress? AddressFromHost(string host)
    {
        if (System.Net.IPAddress.TryParse(host, out var parsed))
            return parsed;
        var dash = host.Split('.')[0];
        var parts = dash.Split('-');
        if (parts.Length == 4 && parts.All(part => byte.TryParse(part, out _)))
            return System.Net.IPAddress.Parse(string.Join('.', parts));
        return null;
    }
}

public sealed class PlexAuthClient(IPlexAuthTransport transport)
{
    public async Task<PlexCreatedPin> CreatePinAsync(string clientIdentifier, CancellationToken cancellationToken)
    {
        var json = await SendAsync(HttpMethod.Post, "https://plex.tv/api/v2/pins?strong=true", clientIdentifier, accountToken: null, "strong=true", cancellationToken);
        return PlexAuthJson.ParseCreatedPin(json, DateTimeOffset.UtcNow);
    }

    public async Task<PlexPinStatus> PollPinAsync(string clientIdentifier, long pinId, CancellationToken cancellationToken)
    {
        var json = await SendAsync(HttpMethod.Get, $"https://plex.tv/api/v2/pins/{pinId}", clientIdentifier, accountToken: null, body: null, cancellationToken);
        return PlexAuthJson.ParsePinStatus(json);
    }

    public async Task<PlexAccountProfile> GetUserAsync(string clientIdentifier, string accountToken, CancellationToken cancellationToken)
    {
        var json = await SendAsync(HttpMethod.Get, "https://plex.tv/api/v2/user", clientIdentifier, accountToken, body: null, cancellationToken);
        return PlexAuthJson.ParseUser(json);
    }

    public async Task<IReadOnlyList<PlexHomeUser>> ListHomeUsersAsync(string clientIdentifier, string accountToken, CancellationToken cancellationToken)
    {
        var json = await SendAsync(HttpMethod.Get, "https://plex.tv/api/v2/home/users", clientIdentifier, accountToken, body: null, cancellationToken);
        return PlexAuthJson.ParseHomeUsers(json);
    }

    public async Task<string> SwitchHomeUserAsync(string clientIdentifier, string accountToken, string uuid, string? pin, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(uuid) || uuid.Length is < 16 or > 40 || uuid.Any(character => !char.IsAsciiHexDigit(character) && character != '-'))
            throw new PlexRequestException("That Plex user id is not valid.");
        var body = string.IsNullOrEmpty(pin) ? null : "pin=" + Uri.EscapeDataString(pin);
        var json = await SendAsync(HttpMethod.Post, "https://plex.tv/api/v2/home/users/" + uuid + "/switch", clientIdentifier, accountToken, body, cancellationToken);
        return PlexAuthJson.ParseSwitchToken(json);
    }

    public async Task<IReadOnlyDictionary<string, string>> GetServerAccessTokensAsync(string clientIdentifier, string accountToken, CancellationToken cancellationToken)
    {
        var json = await SendAsync(HttpMethod.Get, "https://plex.tv/api/v2/resources?includeHttps=1", clientIdentifier, accountToken, body: null, cancellationToken);
        return PlexAuthJson.ParseAccessTokens(json);
    }

    public async Task<IReadOnlyList<OwnedPlexServer>> GetOwnedServersAsync(string clientIdentifier, string accountToken, CancellationToken cancellationToken)
    {
        var json = await SendAsync(HttpMethod.Get, "https://plex.tv/api/v2/resources?includeHttps=1&includeRelay=1", clientIdentifier, accountToken, body: null, cancellationToken);
        return PlexAuthJson.ParseOwnedServers(json);
    }

    internal Task<string> SendForTestsAsync(HttpMethod method, string url, string clientIdentifier, string? accountToken, CancellationToken cancellationToken)
        => SendAsync(method, url, clientIdentifier, accountToken, body: null, cancellationToken);

    private async Task<string> SendAsync(HttpMethod method, string url, string clientIdentifier, string? accountToken, string? body, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || !PlexAuthPolicy.IsAllowed(method, uri))
            throw new PlexRequestException("That Plex account request is not available.");
        if (!string.IsNullOrEmpty(accountToken) && !PlexTokens.IsPlausible(accountToken))
            throw new PlexRequestException("The saved Plex account link can't be used.");

        using var request = new HttpRequestMessage(method, uri);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.TryAddWithoutValidation("X-Plex-Product", "PlexBridge");
        request.Headers.TryAddWithoutValidation("X-Plex-Client-Identifier", clientIdentifier);
        request.Headers.TryAddWithoutValidation("X-Plex-Platform", "Web");
        if (!string.IsNullOrEmpty(accountToken))
            request.Headers.TryAddWithoutValidation("X-Plex-Token", accountToken);
        if (body is not null)
            request.Content = new StringContent(body, Encoding.UTF8, "application/x-www-form-urlencoded");

        HttpResponseMessage response;
        try
        {
            response = await transport.SendAsync(request, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw new PlexRequestException("The request was canceled.");
        }
        catch (OperationCanceledException)
        {
            throw new PlexRequestException("Plex did not respond before the timeout.");
        }
        catch (HttpRequestException exception) when (exception.InnerException is System.Security.Authentication.AuthenticationException)
        {
            throw new PlexRequestException("Plex's certificate could not be validated.");
        }
        catch (HttpRequestException)
        {
            throw new PlexRequestException("Could not reach Plex.");
        }

        using (response)
        {
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                throw new PlexRequestException("Plex rejected the account link.");
            if ((int)response.StatusCode == 429)
                throw new PlexRequestException("Plex asked PlexBridge to slow down.");
            if ((int)response.StatusCode >= 500)
                throw new PlexRequestException("Plex returned an error.");
            if (!response.IsSuccessStatusCode)
                throw new PlexRequestException($"Plex returned HTTP {(int)response.StatusCode}.");
            return await response.Content.ReadAsStringAsync(cancellationToken);
        }
    }
}
