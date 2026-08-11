using McpChannelTelegram.Services;
using McpChannelTelegram.Settings;
using Shouldly;

namespace Tests.Unit.McpChannelTelegram;

public class BotRegistryTests
{
    private static readonly AgentBotConfig[] _testBots =
    [
        new() { AgentId = "jack", BotToken = "123456:ABC-DEF1234ghIkl-zyx57W2v1u123ew11" },
        new() { AgentId = "jonas", BotToken = "654321:ZYX-ABC9876mnOpq-rst42K8j3h987qw22" }
    ];

    [Fact]
    public void GetBotForAgent_UnknownAgent_ThrowsKeyNotFoundException()
    {
        var registry = new BotRegistry(_testBots);

        Should.Throw<KeyNotFoundException>(() => registry.GetBotForAgent("nonexistent"));
    }

    [Fact]
    public void GetAllBots_ReturnsBothBots()
    {
        var registry = new BotRegistry(_testBots);

        var all = registry.GetAllBots();

        all.Count.ShouldBe(2);
        all.Select(b => b.AgentId).ShouldBe(["jack", "jonas"], ignoreOrder: true);
    }

    [Fact]
    public void Lookup_WithUnknownIds_ReturnsNull()
    {
        var registry = new BotRegistry(_testBots);
        registry.RegisterChatAgent(100, "unknown_agent");

        registry.GetBotForChat(999).ShouldBeNull();
        registry.GetBotForChat(100).ShouldBeNull();
    }

    [Fact]
    public void RegisterChatAgent_OverwritesPreviousMapping()
    {
        var registry = new BotRegistry(_testBots);

        registry.RegisterChatAgent(100, "jack");
        registry.RegisterChatAgent(100, "jonas");

        var client = registry.GetBotForChat(100);
        client.ShouldBe(registry.GetBotForAgent("jonas"));
    }
}