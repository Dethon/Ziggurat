using System.Net;
using System.Net.Sockets;

namespace Tests.Integration.Fixtures;

public static class TestPort
{
    // Every number this has ever handed out. The OS picks the port by binding zero, and the bind is
    // released again so the caller can take it themselves — which leaves the number free for the
    // next asker until the caller gets round to it. Serially that gap never closed on anybody; with
    // collections running in parallel two servers were handed the same port and the second died on
    // startup with "address already in use", failing a test that had nothing to do with either.
    //
    // Remembering them is what makes a repeat impossible within this process. A port taken by
    // something outside it is not ours to prevent, which is why the probe stays a real bind rather
    // than a counter.
    private static readonly HashSet<int> _issued = [];
    private static readonly Lock _gate = new();

    public static int GetAvailable()
    {
        // The ephemeral range is tens of thousands of ports wide and a run holds a few dozen, so
        // this settles on the first attempt unless the machine is nearly out of ports — in which
        // case saying so beats handing back a duplicate and failing somewhere else.
        for (var attempt = 0; attempt < 100; attempt++)
        {
            var port = Probe();
            lock (_gate)
            {
                if (_issued.Add(port))
                {
                    return port;
                }
            }
        }

        throw new InvalidOperationException(
            "No unused loopback port after 100 attempts; the ephemeral range looks exhausted.");
    }

    private static int Probe()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}