using System.Net;

namespace Infrastructure.Agents.ChatClients;

// Traffic is about 35 turns a day, so an ordinary gap between two turns is tens of minutes.
// A two-minute lifetime over the default one-minute idle timeout meant every connection was
// already dead when the next turn arrived, and every turn paid a fresh TCP+TLS handshake
// measured at ~230 ms — on the LLM call as well as the embedding call.
//
// The idle timeout is what a keep-alive fires inside of (HostedConnectionKeepAlive); the
// lifetime is the hard cap that recycles a connection whatever its traffic. Recycling lands on
// whichever request first finds the connection expired, a ping or a user's turn, so the pool
// sits cold for up to one ping interval per lifetime: at ten minutes that was 5% of turns,
// and one of them paid a handshake that outlasted the skill preload's deadline. An hour keeps
// that under 1%, and the cap only exists to pick up a host's DNS or balancer rotation, which
// both hosted providers drive themselves with a GOAWAY the pool reconnects after anyway.
public static class HostedConnectionPool
{
    public static readonly TimeSpan ConnectionLifetime = TimeSpan.FromHours(1);
    public static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(5);

    // One handler is one connection pool. Every hosted chat client and the keep-alive that
    // holds a connection open share this one, because warming a pool nobody's turn uses
    // would be worth nothing.
    public static SocketsHttpHandler Shared { get; } = CreateHandler();

    public static SocketsHttpHandler CreateHandler() => new()
    {
        AutomaticDecompression = DecompressionMethods.All,
        PooledConnectionLifetime = ConnectionLifetime,
        PooledConnectionIdleTimeout = IdleTimeout
    };
}