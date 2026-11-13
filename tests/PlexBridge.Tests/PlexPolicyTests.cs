using System.Net;
using System.Net.Sockets;
using PlexBridge.Plex;

namespace PlexBridge.Tests;

public class PlexReadOnlyPolicyTests
{
    [Theory]
    [InlineData("GET", "/identity")]
    [InlineData("GET", "/")]
    [InlineData("GET", "/library/sections")]
    [InlineData("GET", "/library/sections/2")]
    [InlineData("GET", "/library/sections/2/all")]
    [InlineData("GET", "/library/sections/2/all?X-Plex-Container-Start=0&X-Plex-Container-Size=25")]
    public void Allows_identity_and_library_reads(string method, string path)
        => Assert.True(PlexReadOnlyPolicy.IsAllowed(new HttpMethod(method), path));

    [Theory]
    [InlineData("POST", "/library/sections/1/refresh")]
    [InlineData("GET", "/library/sections/1/refresh")]
    [InlineData("PUT", "/library/sections/1")]
    [InlineData("DELETE", "/library/sections/1")]
    [InlineData("GET", "/:/scrobble")]
    [InlineData("GET", "/:/unscrobble")]
    [InlineData("GET", "/:/rate")]
    [InlineData("GET", "/:/progress")]
    [InlineData("GET", "/playQueues")]
    [InlineData("GET", "/status/sessions")]
    [InlineData("GET", "/library/metadata/1")]
    [InlineData("GET", "/library/sections/1/emptyTrash")]
    [InlineData("GET", "/hubs")]
    [InlineData("GET", "/library/sections/1/all?X-Plex-Token=abc")]
    [InlineData("GET", "/identity?token=abc")]
    [InlineData("GET", "/library/sections/../identity")]
    public void Rejects_writes_playback_and_tokens_in_the_query(string method, string path)
        => Assert.False(PlexReadOnlyPolicy.IsAllowed(new HttpMethod(method), path));
}

public class PlexServerUrlTests
{
    [Theory]
    [InlineData("https://plex.example:32400")]
    [InlineData("http://192.168.1.20:32400")]
    [InlineData("http://127.0.0.1:32400")]
    [InlineData("https://media.example.ts.net")]
    [InlineData("http://100.64.1.5:32400/")]
    public void Accepts_admin_server_roots(string input)
    {
        var result = PlexServerUrl.Normalize(input);
        Assert.True(result.Ok, result.Error);
        Assert.DoesNotContain("?", result.Normalized);
        Assert.EndsWith("32400", result.Normalized);
    }

    [Theory]
    [InlineData("https://plex-desktop.lv426.cloud", "https://plex-desktop.lv426.cloud:32400")]
    [InlineData("plex-ugreen.lv426.cloud", "https://plex-ugreen.lv426.cloud:32400")]
    [InlineData("https://plex-desktop.lv426.cloud/web/index.html", "https://plex-desktop.lv426.cloud:32400")]
    [InlineData("https://proxy.example:443", "https://proxy.example:443")]
    public void Bare_and_web_urls_become_a_server_root(string input, string expected)
    {
        var result = PlexServerUrl.Normalize(input);
        Assert.True(result.Ok, result.Error);
        Assert.Equal(expected, result.Normalized);
        if (!input.Contains(":443", StringComparison.Ordinal))
            Assert.Contains("32400", result.Notice);
    }

    [Theory]
    [InlineData("file:///etc/passwd")]
    [InlineData("http://user:pass@host:32400")]
    [InlineData("http://169.254.169.254/")]
    [InlineData("https://plex.example:32400/?X-Plex-Token=abc")]
    [InlineData("https://plex.example:32400/library")]
    [InlineData("javascript:alert(1)")]
    [InlineData("http://metadata.google.internal/")]
    [InlineData("")]
    public void Rejects_unsafe_urls(string input)
    {
        Assert.False(PlexServerUrl.TryNormalize(input, out var normalized, out _));
        Assert.Equal("", normalized);
        Assert.DoesNotContain("abc", normalized);
    }
}

public class PlexAddressRulesTests
{
    [Theory]
    [InlineData("169.254.169.254")]
    [InlineData("169.254.170.2")]
    [InlineData("0.0.0.0")]
    [InlineData("224.0.0.1")]
    [InlineData("255.255.255.255")]
    [InlineData("fd00:ec2::254")]
    public void Blocks_metadata_and_non_unicast(string ip)
        => Assert.True(PlexAddressRules.IsBlocked(IPAddress.Parse(ip)));

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("10.1.2.3")]
    [InlineData("172.16.4.5")]
    [InlineData("192.168.0.8")]
    [InlineData("100.64.0.1")]
    [InlineData("100.127.255.255")]
    [InlineData("8.8.8.8")]
    [InlineData("::1")]
    public void Allows_loopback_lan_tailscale_and_public(string ip)
        => Assert.False(PlexAddressRules.IsBlocked(IPAddress.Parse(ip)));

    [Fact]
    public async Task Dns_guard_rejects_metadata_without_connecting()
    {
        var guard = new DnsPlexAddressGuard();
        var ex = await Assert.ThrowsAsync<PlexRequestException>(() => guard.EnsureAllowedAsync(new Uri("http://169.254.169.254/"), CancellationToken.None));
        Assert.DoesNotContain("token", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Tls_validation_is_not_replaced()
    {
        using var handler = PlexHttp.CreateHandler();
        Assert.Null(handler.SslOptions.RemoteCertificateValidationCallback);
        Assert.False(handler.AllowAutoRedirect);
        Assert.False(handler.UseCookies);
    }

    [Fact]
    public void Blocks_unknown_address_family()
    {
        // IPv6 multicast is blocked. A made-up check keeps the rule from drifting open.
        Assert.True(PlexAddressRules.IsBlocked(IPAddress.Parse("ff02::1")));
        Assert.True(PlexAddressRules.IsBlockedHostName("metadata.google.internal"));
        _ = AddressFamily.InterNetwork;
    }
}
