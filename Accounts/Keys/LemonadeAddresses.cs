using System.Net;
using System.Net.Sockets;

namespace Accounts.Keys;

// Where a household other than the home may keep its Lemonade box: a public https address. The agent
// dials it from inside the deployment, with the household's key, on every turn patched to one of its
// models and every minute to list them, so an address of the deployment's own — a container's name,
// loopback, a private or link-local network, or a name resolving to one — would point the agent at
// its own services.
public static class LemonadeAddresses
{
    public static async Task<bool> IsPublicHttpsAsync(string url, CancellationToken ct)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var address) || address.Scheme != Uri.UriSchemeHttps)
        {
            return false;
        }

        if (IPAddress.TryParse(address.Host.Trim('[', ']'), out var literal))
        {
            return IsPublic(literal);
        }

        if (!address.Host.Contains('.'))
        {
            return false;
        }

        try
        {
            var resolved = await Dns.GetHostAddressesAsync(address.Host, ct);
            return resolved.Length > 0 && resolved.All(IsPublic);
        }
        catch (SocketException)
        {
            return false;
        }
    }

    private static bool IsPublic(IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6)
        {
            ip = ip.MapToIPv4();
        }

        if (IPAddress.IsLoopback(ip) || ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any))
        {
            return false;
        }

        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
        {
            return !(ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || ip.IsIPv6UniqueLocal || ip.IsIPv6Multicast);
        }

        var b = ip.GetAddressBytes();
        return !(b[0] == 10
                 || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
                 || (b[0] == 192 && b[1] == 168)
                 || (b[0] == 169 && b[1] == 254)
                 || (b[0] == 100 && b[1] >= 64 && b[1] <= 127)
                 || b[0] == 0 || b[0] >= 224);
    }
}