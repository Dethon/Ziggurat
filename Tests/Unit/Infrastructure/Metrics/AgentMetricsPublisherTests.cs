using Domain.DTOs.Metrics;
using Infrastructure.Metrics;
using Shouldly;

namespace Tests.Unit.Infrastructure.Metrics;

public class AgentMetricsPublisherTests
{
    // Everything an agent publishes — its tokens, its tool calls, its latencies — is its household's,
    // because the agent was built for the household its turn's identity names. It is keyed by the
    // agent's id, which an owner's rename leaves alone, and carries the name it went by for the
    // figures to be shown under.
    [Fact]
    public void AnEventTheAgentPublishes_NamesItsAgentAndItsHousehold()
    {
        var inner = new RecordingMetricsPublisher();
        var publisher = new AgentMetricsPublisher(inner, "jonas", "Jonas", "home-household");

        publisher.Publish(new ToolCallEvent { ToolName = "mcp__vault__read", DurationMs = 1, Success = true });

        inner.Published.ShouldHaveSingleItem().ShouldSatisfyAllConditions(
            e => e.AgentId.ShouldBe("jonas"),
            e => e.AgentName.ShouldBe("Jonas"),
            e => e.TenantId.ShouldBe("home-household"));
    }
}