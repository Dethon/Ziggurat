using System.Diagnostics;
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
            "test", "user", "test agent", ["http://localhost:1/mcp"],
            new McpClientHandlers(), ct: CancellationToken.None));

        dialing.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(30));
    }
}