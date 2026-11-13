using System.Net;
using System.Net.Sockets;

namespace PlexBridge.Plex;

/// <summary>
/// Decides which addresses an admin-owned Plex server may use.
/// Private LAN, loopback, Tailscale CGNAT (100.64.0.0/10), and ordinary public hosts are allowed.
/// Link-local and cloud metadata addresses are not.
/// </summary>
public static class PlexAddressRules
{
    private static readonly IPAddress AwsMetadataV6 = IPAddress.Parse("fd00:ec2::254");

    public static bool IsBlockedHostName(string host)
    {
        var normalized = host.Trim().TrimEnd('.').ToLowerInvariant();
        return normalized is "metadata.google.internal" or "metadata.google.com" or "instance-data";
    }

    public static bool IsBlocked(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
            address = address.MapToIPv4();

        if (IPAddress.IsLoopback(address))
            return false;

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var bytes = address.GetAddressBytes();
            if (bytes[0] == 0)
                return true;
            if (bytes[0] == 169 && bytes[1] == 254)
                return true;
            if (bytes[0] >= 224)
                return true;
            return false;
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (address.Equals(IPAddress.IPv6None) || address.Equals(AwsMetadataV6))
                return true;
            var bytes = address.GetAddressBytes();
            if (bytes[0] == 0xff)
                return true;
            return false;
        }

        return true;
    }
}
