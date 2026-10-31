using Domain.DTOs.Metrics;
using Infrastructure.Metrics;
using Shouldly;

namespace Tests.Unit.Infrastructure.Metrics;

public class AgentMetricsPublisherTests
{
    // Everything an agent publishes — its tokens, its tool calls, its latencies — is its household's,
    // because the agent was built for the household its turn's identity names.
    [Fact]
    public void AnEventTheAgentPublishes_NamesItsAgentAndItsHousehold()
    {
        var inner = new RecordingMetricsPublisher();
        var publisher = new AgentMetricsPublisher(inner, "jonas", "home-household");

        publisher.Publish(new ToolCallEvent { ToolName = "mcp__vault__read", DurationMs = 1, Success = true });

        inner.Published.ShouldHaveSingleItem().ShouldSatisfyAllConditions(
            e => e.AgentId.ShouldBe("jonas"),
            e => e.TenantId.ShouldBe("home-household"));
    }
}