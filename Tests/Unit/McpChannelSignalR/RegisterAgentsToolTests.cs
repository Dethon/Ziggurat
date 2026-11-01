using Domain.Agents;
using Domain.Contracts;
using Domain.DTOs.Channel;
using Domain.DTOs.WebChat;
using McpChannelSignalR.McpTools;
using Moq;
using Shouldly;

namespace Tests.Unit.McpChannelSignalR;

public class RegisterAgentsToolTests
{
    // The registered agents are the home's, and every household is shown its own: the browsers are
    // told only that something changed, never sent the list.
    [Fact]
    public void McpRun_WithTypedAgents_ReplacesCatalogAndTellsBrowsersWithoutTheList()
    {
        var catalog = new MutableAgentCatalog();
        var sender = new Mock<IHubNotificationSender>();
        var tool = new RegisterAgentsTool(catalog, sender.Object);

        var result = tool.McpRun([new AgentCatalogEntry("jonas", "Jonas", "general")]);

        result.ShouldBe("registered 1 agents");
        catalog.Exists("jonas").ShouldBeTrue();
        sender.Verify(
            s => s.SendAsync("OnAgentsUpdated", It.IsAny<AgentsUpdatedNotification>(), It.IsAny<CancellationToken>()),
            Times.Once);
        sender.VerifyNoOtherCalls();
    }
}