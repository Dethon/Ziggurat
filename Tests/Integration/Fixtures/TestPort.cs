using System.Net;
using System.Net.Sockets;

namespace Tests.Integration.Fixtures;

public static class TestPort
{
    // Every number this has ever handed out. The probe is a real bind, so it can only say the port
    // was free at the moment it asked — remembering the answer is what makes a repeat impossible
    // within this process, whatever the caller does with it afterwards.
    private static readonly HashSet<int> _issued = [];
    private static readonly Lock _gate = new();

    // Where the kernel's own ephemeral range begins. Binding port zero draws from it, and so does
    // every outbound socket and every container publishing a random host port — which is what made
    // the old probe unsafe: it handed back a number the kernel was still free to give to somebody
    // else before the caller bound it. Ports are taken from below this instead, where nothing is
    // assigned unless it is asked for by number.
    public static readonly int EphemeralRangeStart = ReadEphemeralRangeStart();

    // A thousand is far more than a run holds and leaves the band clear of the well-known ports and
    // of the services this suite itself starts on fixed numbers.
    private static readonly int _bandStart = Math.Max(1024, EphemeralRangeStart - 1000);

    private static int _next = _bandStart;

    public static int GetAvailable()
    {
        // Walking the band rather than re-probing one number: a port this run already holds is
        // never offered again, so the walk only pays for ports something else on the machine has.
        while (true)
        {
            int candidate;
            lock (_gate)
            {
                candidate = _next++;
                if (candidate >= EphemeralRangeStart)
                {
                    throw new InvalidOperationException(
                        $"No unused loopback port left below {EphemeralRangeStart}; "
                        + "the reserved band looks exhausted.");
                }

                if (!_issued.Add(candidate))
                {
                    continue;
                }
            }

            if (IsFree(candidate))
            {
                return candidate;
            }
        }
    }

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