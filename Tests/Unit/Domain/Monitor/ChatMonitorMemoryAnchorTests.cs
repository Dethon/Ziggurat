using Domain.Contracts;
using Domain.DTOs.Channel;
using Domain.Monitor;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Moq;
using Shouldly;

namespace Tests.Unit.Domain.Monitor;

// The memory anchor is the persisted message count read while the turn is being built, and it
// is correct only because recall runs before the agent is handed the turn. Move the recall call
// after the run and the extraction window would take the current message out of the persisted
// history AND append the fallback copy, handing the extractor the same turn twice with the real
// one labelled as context. This is the only seam that can see that ordering.
public class ChatMonitorMemoryAnchorTests
{
    [Fact]
    public async Task Monitor_RecallHook_IsHandedAPersistedCountExcludingTheTurnBeingBuilt()
    {
        var agent = MonitorTestMocks.CreateAgent();
        // The hook counts what the thread it is handed has persisted — the same read the real
        // recall makes. The fake agent persists each turn onto the thread as part of running
        // it, so a recall that ran after the run would see its own turn in the count.
        var recall = new RecordingRecallHook();
        var channel = MonitorTestMocks.CreateChannel(
            messages:
            [
                MonitorTestMocks.CreateChannelMessage(content: "first"),
                MonitorTestMocks.CreateChannelMessage(content: "second")
            ]);

        var monitor = new ChatMonitor(
            [channel],
            MonitorTestMocks.CreateAgentFactory(agent),
            MonitorTestMocks.CreateThreadResolver(),
            new Mock<IMetricsPublisher>().Object,
            recall,
            Mock.Of<ILogger<ChatMonitor>>(),
            TestAssertions.Verifier());

        await monitor.Monitor(CancellationToken.None);

        recall.Captures.ShouldBe([("first", 0), ("second", 1)]);
    }

    private sealed class RecordingRecallHook : IMemoryRecallHook
    {
        public List<(string Text, int PersistedCount)> Captures { get; } = [];

        public Task EnrichAsync(
            ChatMessage message,
            Account? account,
            string? conversationId,
            string? agentId,
            AgentSession thread,
            CancellationToken ct)
        {
            var persisted = ((FakeAiAgent.FakeAgentThread)thread).PersistedMessages;
            Captures.Add((message.Text, persisted.Count));
            return Task.CompletedTask;
        }
    }
}