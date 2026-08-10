using Domain.Agents;
using Domain.DTOs.Channel;
using McpChannelTelegram.McpTools;
using Shouldly;

namespace Tests.Unit.McpChannelTelegram;

// Telegram accepts the agent catalogue for one reason: the attachment capability resolution needs
// something to ask. The tool is covered by asserting the catalogue it replaces.
public class TelegramRegisterAgentsToolTests
{
    private readonly MutableAgentCatalog _catalog = new();

    [Fact]
    public void McpRun_ReplacesAnyPreviouslyRegisteredSet()
    {
        var tool = new RegisterAgentsTool(_catalog);
        tool.McpRun([new AgentCatalogEntry("jack", "Jack", null), new AgentCatalogEntry("jill", "Jill", null)]);

        var result = tool.McpRun([new AgentCatalogEntry("jack", "Jack", null, DefaultModel: "a/b")]);

        result.ShouldBe("registered 1 agents");
        _catalog.GetAll().ShouldHaveSingleItem().DefaultModel.ShouldBe("a/b");
        _catalog.Exists("jill").ShouldBeFalse();
    }
}