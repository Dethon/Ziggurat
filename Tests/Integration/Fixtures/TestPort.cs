using System.Net;
using System.Net.Sockets;

namespace Tests.Integration.Fixtures;

public static class TestPort
{
    // Where the kernel's own ephemeral range begins. Binding port zero draws from it, and so does
    // every outbound socket and every container publishing a random host port — which is what made
    // the old probe unsafe: it handed back a number the kernel was still free to give to somebody
    // else before the caller bound it. Ports are taken from below this instead, where nothing is
    // assigned unless it is asked for by number.
    public static readonly int EphemeralRangeStart = ReadEphemeralRangeStart();

    // A thousand-wide band was thought to be far more than a run holds, until the full eval tier
    // walked it to its end: seven servers per run, every scenario retried, and nothing given back.
    // The band stays a thousand; what makes it enough is fixtures releasing their ports on
    // dispose, so the walk only has to cover what is running at once.
    private static readonly PortPool _pool =
        new(Math.Max(1024, EphemeralRangeStart - 1000), EphemeralRangeStart, IsFree);

    public static int GetAvailable() => _pool.Get();

    // For a fixture that has stopped whatever it bound; the number becomes issuable again, and the
    // probe still has the last word when it is.
    public static void Release(int port) => _pool.Release(port);

    private static bool IsFree(int port)
    {
        try
        {
            using var listener = new TcpListener(IPAddress.Loopback, port);
            listener.Start();
            listener.Stop();
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    // Linux states the range in a two-number file; anywhere else, the IANA-registered start of the
    // dynamic range is the safe assumption. Either way this only has to be a lower bound on what
    // the kernel allocates, because the band sits underneath it.
    private static int ReadEphemeralRangeStart()
    {
        const int fallback = 32768;
        try
        {
            const string path = "/proc/sys/net/ipv4/ip_local_port_range";
            if (!File.Exists(path))
            {
                return fallback;
            }

            var low = File.ReadAllText(path).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            return low.Length > 0 && int.TryParse(low[0], out var parsed) ? parsed : fallback;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return fallback;
        }
    }
}