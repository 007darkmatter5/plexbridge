using System.Net;
using System.Net.Sockets;

namespace PlexBridge.Plex;

public static class PlexHttp
{
    public const string ClientName = "plexbridge-read";
    public const string WriteClientName = "plexbridge-write";
    public const string AuthClientName = "plexbridge-auth";
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(20);
    public const int MaxResponseBytes = 2 * 1024 * 1024;

    /// <summary>
    /// TLS validation stays at the platform default. Redirects are handled by <see cref="PlexReadClient"/>
    /// so a Plex server cannot send the token to another host.
    /// </summary>
    public static SocketsHttpHandler CreateHandler()
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.All,
            ConnectTimeout = TimeSpan.FromSeconds(10),
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            UseCookies = false
        };
        if (handler.SslOptions.RemoteCertificateValidationCallback is not null)
            throw new InvalidOperationException("TLS certificate validation must stay on.");
        return handler;
    }
}

public interface IPlexTransport
{
    Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken);
}

public sealed class HttpClientPlexTransport(IHttpClientFactory factory) : IPlexTransport
{
    public Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        => factory.CreateClient(PlexHttp.ClientName).SendAsync(request, cancellationToken);
}

/// <summary>Writes use their own HTTP client and timeout. Reads stay on <see cref="PlexHttp.ClientName"/>.</summary>
public interface IPlexWriteTransport : IPlexTransport;

public sealed class HttpClientPlexWriteTransport(IHttpClientFactory factory) : IPlexWriteTransport
{
    public Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        => factory.CreateClient(PlexHttp.WriteClientName).SendAsync(request, cancellationToken);
}

/// <summary>Fake and test transports serve reads and writes on the same stand-in.</summary>
public sealed class ForwardingPlexWriteTransport(IPlexTransport inner) : IPlexWriteTransport
{
    public Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        => inner.SendAsync(request, cancellationToken);
}

public interface IPlexAuthTransport
{
    Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken);
}

public sealed class HttpClientPlexAuthTransport(IHttpClientFactory factory) : IPlexAuthTransport
{
    public Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        => factory.CreateClient(PlexHttp.AuthClientName).SendAsync(request, cancellationToken);
}

public interface IPlexAddressGuard
{
    Task EnsureAllowedAsync(Uri baseUri, CancellationToken cancellationToken);
}

public sealed class DnsPlexAddressGuard : IPlexAddressGuard
{
    public async Task EnsureAllowedAsync(Uri baseUri, CancellationToken cancellationToken)
    {
        if (PlexAddressRules.IsBlockedHostName(baseUri.IdnHost))
            throw new PlexRequestException("That host is not allowed.");

        if (baseUri.HostNameType is UriHostNameType.IPv4 or UriHostNameType.IPv6)
        {
            EnsureAddress(IPAddress.Parse(baseUri.IdnHost));
            return;
        }

        IPAddress[] addresses;
        try
        {
            addresses = await Dns.GetHostAddressesAsync(baseUri.IdnHost, cancellationToken);
        }
        catch (Exception exception) when (exception is SocketException or ArgumentException)
        {
            throw new PlexRequestException("The server address could not be resolved.");
        }

        if (addresses.Length == 0)
            throw new PlexRequestException("The server address could not be resolved.");

        foreach (var address in addresses)
            EnsureAddress(address);
    }

    private static void EnsureAddress(IPAddress address)
    {
        if (IPAddress.IsLoopback(address))
            return;
        if (PlexAddressRules.IsBlocked(address))
            throw new PlexRequestException("That address is not allowed.");
    }
}

/// <summary>Used by tests that replace HTTP with a fake handler. Not registered in the app.</summary>
internal sealed class AllowAllAddressGuard : IPlexAddressGuard
{
    public Task EnsureAllowedAsync(Uri baseUri, CancellationToken cancellationToken) => Task.CompletedTask;
}
