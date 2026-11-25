using System.Net;
using System.Net.Sockets;

namespace McpServerApps.Checks;

// Where the check browser's egress may dial: global unicast on the public internet, nothing else. Default
// deny: an address is refused unless it is in the global unicast space and in none of the ranges carved out
// of it for private networks, the Docker host's bridges, loopback, link-local, CGNAT, benchmarking,
// documentation, multicast or the future; an IPv4 address written as IPv6 (mapped, NAT64, 6to4) is judged as
// itself. A Docker host on a public address is reached on its public ports alone, which the internet
// reaches too.
public static class EgressAddresses
{
    private static readonly (IPAddress Network, int Bits)[] _refusedV4 =
    [
        (IPAddress.Parse("0.0.0.0"), 8),
        (IPAddress.Parse("10.0.0.0"), 8),
        (IPAddress.Parse("100.64.0.0"), 10),
        (IPAddress.Parse("127.0.0.0"), 8),
        (IPAddress.Parse("169.254.0.0"), 16),
        (IPAddress.Parse("172.16.0.0"), 12),
        (IPAddress.Parse("192.0.0.0"), 24),
        (IPAddress.Parse("192.0.2.0"), 24),
        (IPAddress.Parse("192.88.99.0"), 24),
        (IPAddress.Parse("192.168.0.0"), 16),
        (IPAddress.Parse("198.18.0.0"), 15),
        (IPAddress.Parse("198.51.100.0"), 24),
        (IPAddress.Parse("203.0.113.0"), 24),
        (IPAddress.Parse("224.0.0.0"), 4),
        (IPAddress.Parse("240.0.0.0"), 4)
    ];

    // Inside 2000::/3, the global unicast space: documentation, Teredo and the other protocol assignments
    // (2001::/23) and 6to4, whose embedded IPv4 address is judged instead.
    private static readonly (IPAddress Network, int Bits)[] _refusedV6 =
    [
        (IPAddress.Parse("2001::"), 23),
        (IPAddress.Parse("2001:db8::"), 32)
    ];

    private static readonly IPAddress _globalUnicast = IPAddress.Parse("2000::");
    private static readonly IPAddress _sixToFour = IPAddress.Parse("2002::");
    private static readonly IPAddress _nat64 = IPAddress.Parse("64:ff9b::");

    public static bool IsPublic(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
        {
            return IsPublic(address.MapToIPv4());
        }

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            return !_refusedV4.Any(range => Within(address, range.Network, range.Bits));
        }

        if (address.AddressFamily != AddressFamily.InterNetworkV6)
        {
            return false;
        }

        var bytes = address.GetAddressBytes();
        if (Within(address, _nat64, 96))
        {
            return IsPublic(new IPAddress(bytes[12..16]));
        }

        if (Within(address, _sixToFour, 16))
        {
            return IsPublic(new IPAddress(bytes[2..6]));
        }

        return Within(address, _globalUnicast, 3) && !_refusedV6.Any(range => Within(address, range.Network, range.Bits));
    }

    private static bool Within(IPAddress address, IPAddress network, int bits)
    {
        var a = address.GetAddressBytes();
        var n = network.GetAddressBytes();
        return a.Length == n.Length
               && Enumerable.Range(0, bits / 8).All(i => a[i] == n[i])
               && (bits % 8 == 0 || ((a[bits / 8] ^ n[bits / 8]) & (0xFF << (8 - bits % 8)) & 0xFF) == 0);
    }
}