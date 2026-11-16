using Domain.DTOs;
using Shouldly;

namespace Tests.Unit.Domain.Agents;

// Which agent answers a message that names none is a configured decision. The agent array is a
// catalogue ordered for display, and nothing here may read it. The deployment's defaults are per
// channel; any other channel falls back to its household's default (ChatMonitorAgentRoutingTests).
public class AgentDefaultsTests
{
    private static readonly AgentDefaults _defaults = new()
    {
        ByChannel = new Dictionary<string, string> { ["voice"] = "nabu" }
    };

    [Theory]
    [InlineData("voice", "nabu")]
    [InlineData("VOICE", "nabu")]
    [InlineData("telegram", null)]
    [InlineData(null, null)]
    public void For_Channel_ResolvesItsOwnDefaultAlone(string? channelId, string? expected)
    {
        _defaults.For(channelId).ShouldBe(expected);
    }

    [Fact]
    public void For_NothingConfigured_ResolvesNothing()
    {
        new AgentDefaults().For("telegram").ShouldBeNull();
    }
}