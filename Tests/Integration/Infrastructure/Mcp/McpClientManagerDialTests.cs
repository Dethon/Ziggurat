using System.Diagnostics;
using Infrastructure.Agents;
using Infrastructure.Agents.Mcp;
using ModelContextProtocol.Client;
using Shouldly;

namespace Tests.Integration.Infrastructure.Mcp;

// The agent's dial at its tool servers, against an address nothing answers.
public class McpClientManagerDialTests
{
    [Fact]
    public async Task CreateAsync_WhereNothingIsListening_GivesUpWithoutWaitingOutTheSdkDefault()
    {
        // A tool server that is down is dialled on the way into a session, so what this costs is
        // paid by the turn that is waiting for it. The SDK's own initialization timeout is a
        // minute, and the retry around this dial can only multiply that, so the handshake is
        // bounded here for the same reason it is bounded on the channel connection.
        var dialing = Stopwatch.StartNew();

        await Should.ThrowAsync<Exception>(() => McpClientManager.CreateAsync(
            "test", "user", "test agent", [McpServerEndpoint.Configured("http://localhost:1/mcp")],
            new McpClientHandlers(), ct: CancellationToken.None));

        dialing.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(30));
    }

    // A call to a dialled server lasts as long as its caller lets it: an approval waits for a
    // person, and HttpClient's own 100 s default refused every one nobody answered within it
    // (prod, 2026-11-26: a publish asked at 11:57:45, "rejected" at 11:59:25 with nobody's answer).
    // The tunnel's client is unbounded the same way (OpenTunnel).
    [Fact]
    public void ADialledServer_IsNeverCutByTheHttpClientsOwnTimeout()
    {
        using var client = McpClientManager.DialledHttpClient();

        client.Timeout.ShouldBe(Timeout.InfiniteTimeSpan);
    }
}