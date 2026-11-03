using Domain.Agents;
using Domain.Contracts;
using Domain.DTOs;
using Domain.DTOs.Channel;
using Domain.Monitor;
using Microsoft.Extensions.Logging;
using Moq;
using Shouldly;

namespace Tests.Unit.Domain.Monitor;

// A conversation's agent is built by its first turn and outlives it, so what an owner changes about
// that agent — a feature turned off, its instructions, its name — reached only conversations begun
// afterwards: with timers off a member's open conversation still set a timer, and an instruction to
// sign every reply went unsigned in a shared conversation begun before it. A turn now asks the agent
// it is about to run whether the household still defines it as it was built, and a stale one is
// replaced, its history read back from the store, before the turn runs.
public class ChatMonitorStaleAgentTests
{
    private sealed class OneAgentPerBuild(params FakeAiAgent[] agents) : IAgentFactory
    {
        private int _built;

        public int Built => _built;

        public Task<DisposableAgent> CreateAsync(
            AgentKey agentKey, string userId, string? agentId, IToolApprovalHandler approvalHandler, Account? account = null,
            string? tenantId = null, CancellationToken ct = default, string? channelId = null) =>
            Task.FromResult<DisposableAgent>(agents[Interlocked.Increment(ref _built) - 1]);

        public DisposableAgent CreateSubAgent(
            SubAgentDefinition definition, IToolApprovalHandler approvalHandler, SpawnContext spawn) =>
            throw new NotImplementedException();
    }

    private static async Task RunTwoTurnsAsync(IAgentFactory factory)
    {
        var channel = MonitorTestMocks.CreateChannel(messages:
        [
            MonitorTestMocks.CreateChannelMessage(content: "first"),
            MonitorTestMocks.CreateChannelMessage(content: "second")
        ]);
        var monitor = new ChatMonitor(
            [channel], factory, MonitorTestMocks.CreateThreadResolver(), new Mock<IMetricsPublisher>().Object,
            null, new Mock<ILogger<ChatMonitor>>().Object, TestAssertions.Verifier());

        await monitor.Monitor(CancellationToken.None);
    }

    [Fact]
    public async Task AnAgentTheHouseholdNoLongerDefinesAsBuilt_IsReplacedBeforeTheNextTurn()
    {
        var built = new FakeAiAgent { Stale = true };
        var rebuilt = new FakeAiAgent();
        var factory = new OneAgentPerBuild(built, rebuilt);

        await RunTwoTurnsAsync(factory);

        factory.Built.ShouldBe(2);
        built.ReceivedMessages.ShouldHaveSingleItem().Last().Text.ShouldBe("first");
        rebuilt.ReceivedMessages.ShouldHaveSingleItem().Last().Text.ShouldBe("second");
        built.DisposeCalls.ShouldBe(1);
        rebuilt.WarmupCalls.ShouldBe(1);
    }

    [Fact]
    public async Task AnAgentStillAsBuilt_RunsEveryTurn()
    {
        var built = new FakeAiAgent();
        var factory = new OneAgentPerBuild(built);

        await RunTwoTurnsAsync(factory);

        factory.Built.ShouldBe(1);
        built.ReceivedMessages.Count.ShouldBe(2);
    }
}