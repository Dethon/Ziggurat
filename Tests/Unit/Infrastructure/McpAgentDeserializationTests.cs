using System.Text.Json;
using Domain.Agents;
using Domain.Contracts;
using Infrastructure.Agents;
using Infrastructure.Agents.ChatClients;
using Infrastructure.Metrics;
using Microsoft.Extensions.AI;
using Moq;
using Shouldly;

namespace Tests.Unit.Infrastructure;

public class McpAgentDeserializationTests : IAsyncDisposable
{
    private readonly McpAgent _agent;

    public McpAgentDeserializationTests()
    {
        var chatClient = new Mock<IChatClient>();
        var stateStore = new Mock<IThreadStateStore>();
        _agent = new McpAgent(
            TestAgentSpec.Default,
            chatClient.Object,
            stateStore.Object,
            NoOpMetricsPublisher.Instance,
            TimeProvider.System,
            [],
            []);
    }

    public async ValueTask DisposeAsync()
    {
        await _agent.DisposeAsync();
    }

    [Fact]
    public async Task DeserializeSession_WithProperlySerializedSession_PreservesStateBag()
    {
        var agentKey = new AgentKey("789:101", "test-agent");
        var serialized = JsonSerializer.SerializeToElement(agentKey.ToString());
        var session = await _agent.DeserializeSessionAsync(serialized);

        // Serialize the session (this produces the new format with stateBag)
        var reSerialized = await _agent.SerializeSessionAsync(session);

        var restored = await _agent.DeserializeSessionAsync(reSerialized);

        restored.StateBag.TryGetValue<string>(RedisChatMessageStore.StateKey, out var key).ShouldBeTrue();
        key.ShouldBe(agentKey.ToString());
    }

}