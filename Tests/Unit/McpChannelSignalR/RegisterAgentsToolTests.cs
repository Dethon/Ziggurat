using Domain.Agents;
using Domain.Contracts;
using Domain.DTOs.Channel;
using McpChannelSignalR.McpTools;
using Moq;
using Shouldly;

namespace Tests.Unit.McpChannelSignalR;

public class RegisterAgentsToolTests
{
    [Fact]
    public void McpRun_WithTypedAgents_ReplacesCatalogAndBroadcastsUpdate()
    {
        var catalog = new MutableAgentCatalog();
        var sender = new Mock<IHubNotificationSender>();
        var tool = new RegisterAgentsTool(catalog, sender.Object);

        var result = tool.McpRun([new AgentCatalogEntry("jonas", "Jonas", "general")]);

        result.ShouldBe("registered 1 agents");
        catalog.Exists("jonas").ShouldBeTrue();
        sender.Verify(
            s => s.SendAsync("OnAgentsUpdated", It.IsAny<object>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }
}