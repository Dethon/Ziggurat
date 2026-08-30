using Domain.Agents;
using Domain.DTOs.Channel;
using Mcp.Hosting;
using Shouldly;

namespace Tests.Unit.Mcp.Hosting;

// The one catalog-writing register_agents: Telegram, voice and scheduling all register this
// shared tool instead of carrying byte-identical copies. The SignalR variant (hub broadcast) and
// the library one (no-op) stay their servers' own.
public class RegisterAgentsToolTests
{
    [Fact]
    public void McpRun_ReplacesAnyPreviouslyRegisteredSet()
    {
        var catalog = new MutableAgentCatalog();
        catalog.Replace([new AgentCatalogEntry("stale", "Stale", "general")]);
        var tool = new RegisterAgentsTool(catalog);

        var result = tool.McpRun([
            new AgentCatalogEntry("jonas", "Jonas", "general"),
            new AgentCatalogEntry("jack", "Jack", "downloads")
        ]);

        result.ShouldBe("registered 2 agents");
        catalog.Exists("jonas").ShouldBeTrue();
        catalog.Exists("jack").ShouldBeTrue();
        catalog.Exists("stale").ShouldBeFalse();
    }
}