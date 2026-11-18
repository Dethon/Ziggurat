using System.Net;
using System.Net.Sockets;

namespace Tests.Integration.Fixtures;

// An address nothing answers, failing at once on every host: a loopback listener that resets each
// connection it accepts. A port assumed closed is not one — on WSL a connect to 127.0.0.1:1 is
// dropped rather than refused, and the caller waits out its whole connect timeout.
public static class DeadEnd
{
    private static readonly Lazy<int> _port = new(() =>
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        _ = Task.Run(async () =>
        {
            while (true)
            {
                using var connection = await listener.AcceptTcpClientAsync();
                connection.Client.LingerState = new LingerOption(true, 0);
            }
        });
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    });

    public static int Port => _port.Value;

    public static string Url(string path = "/") => $"http://127.0.0.1:{Port}{path}";
}