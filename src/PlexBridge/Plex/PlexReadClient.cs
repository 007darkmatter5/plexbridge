using System.Net;
using System.Net.Http.Headers;
using System.Text;
using PlexBridge.Security;

namespace PlexBridge.Plex;

/// <summary>Read-only Plex Media Server client. The token is sent only as the X-Plex-Token header.</summary>
public sealed class PlexReadClient(IPlexTransport transport, IPlexAddressGuard addresses)
{
    public async Task<PlexIdentityInfo> GetIdentityAsync(Uri baseUri, string token, CancellationToken cancellationToken)
    {
        var identity = PlexJsonParser.ParseIdentity(await ReadAsync(baseUri, token, "/identity", cancellationToken));
        try
        {
            var root = PlexJsonParser.ParseIdentity(await ReadAsync(baseUri, token, "/", cancellationToken));
            identity = identity.WithFriendlyName(root.FriendlyName);
            identity = new PlexIdentityInfo(
                identity.MachineIdentifier ?? root.MachineIdentifier,
                identity.Version ?? root.Version,
                identity.FriendlyName);
        }
        catch (PlexRequestException)
        {
            // The identity document is enough when the root document is unavailable.
        }

        return identity;
    }

    public async Task<IReadOnlyList<PlexSectionInfo>> GetSectionsAsync(Uri baseUri, string token, CancellationToken cancellationToken)
    {
        var json = await ReadAsync(baseUri, token, "/library/sections", cancellationToken);
        return PlexJsonParser.ParseSections(json);
    }

    public async Task<PlexItemPage> GetPageAsync(Uri baseUri, string token, string sectionKey, int start, int size, CancellationToken cancellationToken)
    {
        if (sectionKey.Length is 0 or > 10 || !sectionKey.All(char.IsDigit))
            throw new PlexRequestException("The library id is not valid.");
        if (start < 0)
            start = 0;
        size = Math.Clamp(size, 0, 100);
        var relative = $"/library/sections/{sectionKey}/all?X-Plex-Container-Start={start}&X-Plex-Container-Size={size}";
        return PlexJsonParser.ParseItems(await ReadAsync(baseUri, token, relative, cancellationToken));
    }

    public async Task<IReadOnlyList<PlexLibraryItem>> ListAllItemsAsync(Uri baseUri, string token, string sectionKey, int pageSize, CancellationToken cancellationToken)
    {
        pageSize = Math.Clamp(pageSize, 1, 100);
        var items = new List<PlexLibraryItem>();
        var start = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var page = await GetPageAsync(baseUri, token, sectionKey, start, pageSize, cancellationToken);
            items.AddRange(page.Items);
            if (page.Items.Count == 0)
                break;
            start += page.Items.Count;
            if (start >= page.Total || page.Items.Count < pageSize)
                break;
            if (items.Count > 100_000)
                throw new PlexRequestException("That library is larger than this read will collect.");
        }

        return items;
    }

    internal Task<string> ReadForTestsAsync(Uri baseUri, string token, string relative, CancellationToken cancellationToken)
        => ReadAsync(baseUri, token, relative, cancellationToken);

    private async Task<string> ReadAsync(Uri baseUri, string token, string relative, CancellationToken cancellationToken)
    {
        if (!PlexTokens.IsPlausible(token))
            throw new PlexRequestException("A Plex token is required.");
        if (PlexAuthPolicy.IsAccountHost(baseUri))
            throw new PlexRequestException("Account requests do not use the media client.");

        PlexReadOnlyPolicy.EnsureAllowed(HttpMethod.Get, relative);
        await addresses.EnsureAllowedAsync(baseUri, cancellationToken);

        var current = new Uri(baseUri, relative);
        for (var hop = 0; hop < 3; hop++)
        {
            RejectTokenInUri(current);
            using var request = new HttpRequestMessage(HttpMethod.Get, current);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            request.Headers.UserAgent.ParseAdd("PlexBridge");
            request.Headers.TryAddWithoutValidation("X-Plex-Product", "PlexBridge");
            request.Headers.TryAddWithoutValidation("X-Plex-Client-Identifier", "plexbridge");
            request.Headers.TryAddWithoutValidation("X-Plex-Platform", "Web");
            request.Headers.TryAddWithoutValidation("X-Plex-Token", token.Trim());

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
                throw new PlexRequestException("The server did not respond before the timeout.");
            }
            catch (HttpRequestException exception) when (exception.InnerException is System.Security.Authentication.AuthenticationException)
            {
                throw new PlexRequestException("The server certificate could not be validated.");
            }
            catch (HttpRequestException)
            {
                throw new PlexRequestException("Could not connect to the server.");
            }

            using (response)
            {
                var status = (int)response.StatusCode;
                if (status is 301 or 302 or 303 or 307 or 308)
                {
                    current = NextHop(current, response.Headers.Location);
                    PlexReadOnlyPolicy.EnsureAllowed(HttpMethod.Get, current.PathAndQuery);
                    continue;
                }

                if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                    throw new PlexRequestException("The server rejected the token.");
                if (response.StatusCode == HttpStatusCode.NotFound)
                    throw new PlexRequestException("The Plex path was not found.");
                if (status >= 500 || response.StatusCode == HttpStatusCode.RequestTimeout)
                    throw new PlexRequestException("The server returned an error.");
                if (!response.IsSuccessStatusCode)
                    throw new PlexRequestException($"The server returned HTTP {status}.");

                return await ReadBodyAsync(response, cancellationToken);
            }
        }

        throw new PlexRequestException("The server redirected too many times.");
    }

    private static Uri NextHop(Uri current, Uri? location)
    {
        if (location is null)
            throw new PlexRequestException("The server redirected without a destination.");
        var next = location.IsAbsoluteUri ? location : new Uri(current, location);
        var sameHost = string.Equals(current.IdnHost, next.IdnHost, StringComparison.OrdinalIgnoreCase);
        var sameScheme = string.Equals(current.Scheme, next.Scheme, StringComparison.OrdinalIgnoreCase);
        var httpsUpgrade = string.Equals(current.Scheme, "http", StringComparison.OrdinalIgnoreCase)
            && string.Equals(next.Scheme, "https", StringComparison.OrdinalIgnoreCase);
        var allowedScheme = string.Equals(next.Scheme, "http", StringComparison.OrdinalIgnoreCase)
            || string.Equals(next.Scheme, "https", StringComparison.OrdinalIgnoreCase);
        if (!sameHost || !allowedScheme || (!sameScheme && !httpsUpgrade) || !string.IsNullOrEmpty(next.UserInfo))
            throw new PlexRequestException("The server redirected to a different host, which is not allowed.");
        RejectTokenInUri(next);
        return next;
    }

    private static void RejectTokenInUri(Uri uri)
    {
        if (uri.Query.Contains("X-Plex-Token", StringComparison.OrdinalIgnoreCase)
            || uri.Query.Contains("token=", StringComparison.OrdinalIgnoreCase))
            throw new PlexRequestException("The token has to stay in the request header.");
    }

    private static async Task<string> ReadBodyAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var advertised = response.Content.Headers.ContentLength;
        if (advertised > PlexHttp.MaxResponseBytes)
            throw new PlexRequestException("The server response was too large to read.");

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var copy = new MemoryStream();
        var buffer = new byte[8192];
        var total = 0;
        while (true)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken);
            if (read == 0)
                break;
            total += read;
            if (total > PlexHttp.MaxResponseBytes)
                throw new PlexRequestException("The server response was too large to read.");
            copy.Write(buffer, 0, read);
        }

        return Encoding.UTF8.GetString(copy.ToArray());
    }
}
