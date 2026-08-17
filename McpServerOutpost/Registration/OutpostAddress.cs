using System.Net;
using System.Net.Sockets;

namespace McpServerOutpost.Registration;

// The address the hub will be told to dial. A person starting a binary on their laptop should not
// have to know which of their machine's interfaces the hub can reach them on, so it is worked out
// from the route toward the hub — which is right on a flat network and wrong on a multi-homed one,
// and that is what --advertise is for.
//
// Neither the autodetection nor the override yielding a usable address is a startup failure with a
// message saying so, rather than a registration of something nothing can reach: a machine that
// registered an unreachable address looks exactly like a machine that is asleep, and nobody would
// ever find out which it was.
public static class OutpostAddress
{
    public static string Resolve(string hub, string? advertise, int port) =>
        Resolve(hub, advertise, port, RouteToward);

    internal static string Resolve(
        string hub, string? advertise, int port, Func<string, IPAddress?> routeToward)
    {
        ArgumentNullException.ThrowIfNull(routeToward);
        var hubHost = HostOf(hub);
        var host = string.IsNullOrWhiteSpace(advertise)
            ? routeToward(hubHost)?.ToString()
            : advertise.Trim();

        return Usable(host)
            ? new UriBuilder("http", Bracketed(host!), port, "/mcp").Uri.ToString()
            : throw new InvalidOperationException(
                $"This outpost cannot work out an address the hub at '{hub}' could dial it back on"
                + (string.IsNullOrWhiteSpace(advertise) ? "" : $" ('{advertise}' is not one)")
                + ". Pass --advertise with the address of the interface the hub can reach, "
                + "rather than registering something nothing can answer.");
    }

    // The route the operating system would take toward the hub, read off a datagram socket that is
    // connected and never written to — no packet leaves, and the kernel has still had to choose the
    // interface. A hub on this same machine legitimately answers loopback, which is what a
    // developer running both wants.
    private static IPAddress? RouteToward(string hubHost)
    {
        try
        {
            using var probe = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            probe.Connect(hubHost, 65530);
            return (probe.LocalEndPoint as IPEndPoint)?.Address;
        }
        catch (Exception ex) when (ex is SocketException or ArgumentException)
        {
            return null;
        }
    }

    private static bool Usable(string? host) =>
        !string.IsNullOrWhiteSpace(host)
        && !(IPAddress.TryParse(host, out var parsed)
             && (parsed.Equals(IPAddress.Any) || parsed.Equals(IPAddress.IPv6Any)));

    // A bare IPv6 address is not a URI host until it is in brackets, and UriBuilder does not add
    // them.
    private static string Bracketed(string host) =>
        IPAddress.TryParse(host, out var parsed) && parsed.AddressFamily == AddressFamily.InterNetworkV6
        && !host.StartsWith('[')
            ? $"[{host}]"
            : host;

    // The hub arrives as a URL, and the route is toward its host. A bare host or an
    // unparseable value is taken as the host itself, so a typo fails at the dial with a message
    // naming it rather than here with one about URI syntax.
    private static string HostOf(string hub) =>
        Uri.TryCreate(hub, UriKind.Absolute, out var uri) ? uri.Host : hub;
}