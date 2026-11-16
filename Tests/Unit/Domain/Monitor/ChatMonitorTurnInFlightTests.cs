using Domain.Agents;
using Domain.Contracts;
using Domain.Monitor;
using Microsoft.Extensions.Logging;
using Moq;
using Shouldly;
using Tests.Unit.Domain;

namespace Tests.Unit.Domain.Monitor;

// The history store clears a turn's in-flight mark as it keeps the turn, but a turn whose reply was let
// go of unread never reaches that: the group clears it as the turn's stream ends, however it ends.
public class ChatMonitorTurnInFlightTests
{
    [Fact]
    public async Task ATurnsEnd_ClearsItsConversationsInFlightMark()
    {
        var store = new Mock<IThreadStateStore>();
        var message = MonitorTestMocks.CreateChannelMessage(
            conversationId: "conv-1", channelId: "signalr", agentId: "jonas", sender: "test");
        var monitor = new ChatMonitor(
            [MonitorTestMocks.CreateChannel("signalr", message)],
            MonitorTestMocks.CreateAgentFactory(MonitorTestMocks.CreateAgent()),
            new ChatThreadResolver(store.Object),
            new Mock<IMetricsPublisher>().Object,
            null,
            new Mock<ILogger<ChatMonitor>>().Object,
            TestAssertions.Verifier());

        await monitor.Monitor(CancellationToken.None);

        store.Verify(s => s.ClearTurnInFlightAsync(new AgentKey("conv-1", "jonas").ToString()), Times.AtLeastOnce);
    }
}