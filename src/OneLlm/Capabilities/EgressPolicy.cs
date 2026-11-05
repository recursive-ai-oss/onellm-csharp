using System.Net;
using System.Net.Sockets;

namespace OneLlm.Access;

/// <summary>Address classification for network egress checks.</summary>
internal static class EgressPolicy
{
    /// <summary>
    /// True for an address that is not a globally routable unicast address: loopback, private, link-local (including the cloud
    /// metadata address 169.254.169.254), shared, unique-local, multicast, reserved and documentation ranges. IPv6 addresses that embed an
    /// IPv4 address (mapped, NAT64 and 6to4) are judged by the embedded address.
    /// </summary>
    internal static bool IsSpecialAddress(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        IPAddress a = HostPattern.Normalize(address);
        byte[] b = a.GetAddressBytes();
        if (a.AddressFamily == AddressFamily.InterNetwork)
        {
            return IsSpecialV4(b[0], b[1], b[2]);
        }

        if (a.AddressFamily != AddressFamily.InterNetworkV6)
        {
            return true;
        }

        if (a.Equals(IPAddress.IPv6Any) || a.Equals(IPAddress.IPv6Loopback) || a.IsIPv6LinkLocal || a.IsIPv6SiteLocal || a.IsIPv6Multicast)
        {
            return true;
        }

        if ((b[0] & 0xFE) == 0xFC)
        {
            return true; // fc00::/7 unique local
        }

        if (b[0] == 0x20 && b[1] == 0x01 && (b[2] == 0x00 && b[3] == 0x00 || b[2] == 0x0D && b[3] == 0xB8))
        {
            return true; // 2001::/32 Teredo and 2001:db8::/32 documentation
        }

        if (b[0] == 0x01 && b[1] == 0x00 && b[2] == 0 && b[3] == 0 && b[4] == 0 && b[5] == 0 && b[6] == 0 && b[7] == 0)
        {
            return true; // 100::/64 discard
        }

        if (b[0] == 0x00 && b[1] == 0x64 && b[2] == 0xFF && b[3] == 0x9B && b[4] == 0 && b[5] == 0 && b[6] == 0 && b[7] == 0 && b[8] == 0 && b[9] == 0 && b[10] == 0 && b[11] == 0)
        {
            return IsSpecialV4(b[12], b[13], b[14]); // 64:ff9b::/96 NAT64
        }

        if (b[0] == 0x20 && b[1] == 0x02)
        {
            return IsSpecialV4(b[2], b[3], b[4]); // 2002::/16 6to4
        }

        return false;
    }

    private static bool IsSpecialV4(byte b0, byte b1, byte b2) =>
        b0 == 0
        || b0 == 10
        || b0 == 127
        || b0 >= 224 // multicast and reserved, including 255.255.255.255
        || b0 == 100 && b1 is >= 64 and <= 127
        || b0 == 169 && b1 == 254
        || b0 == 172 && b1 is >= 16 and <= 31
        || b0 == 192 && b1 == 168
        || b0 == 192 && b1 == 0 && b2 is 0 or 2
        || b0 == 198 && b1 is 18 or 19
        || b0 == 198 && b1 == 51 && b2 == 100
        || b0 == 203 && b1 == 0 && b2 == 113;

    /// <summary>The host as the allow-list sees it: lower case, punycode, no brackets, no trailing dot.</summary>
    internal static string CanonicalHost(Uri uri)
    {
        string host = uri.IdnHost;
        if (host.Length > 1 && host[0] == '[' && host[^1] == ']')
        {
            host = host[1..^1];
        }

        int zone = host.IndexOf('%', StringComparison.Ordinal);
        if (zone >= 0)
        {
            host = host[..zone];
        }

        return host.TrimEnd('.').ToLowerInvariant();
    }
}
