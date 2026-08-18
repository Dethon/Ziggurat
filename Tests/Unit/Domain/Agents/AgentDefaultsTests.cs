using Domain.DTOs;
using Shouldly;

namespace Tests.Unit.Domain.Agents;

// Which agent answers a message that names none is a configured decision. The agent array is a
// catalogue ordered for display, and nothing here may read it.
public class AgentDefaultsTests
{
    private static readonly AgentDefaults _defaults = new()
    {
        Fallback = "jonas",
        ByChannel = new Dictionary<string, string> { ["voice"] = "nabu" }
    };

    [Theory]
    [InlineData("voice", "nabu")]
    [InlineData("VOICE", "nabu")]
    [InlineData("telegram", "jonas")]
    [InlineData(null, "jonas")]
    public void For_Channel_ResolvesItsDefaultOrTheFallback(string? channelId, string expected)
    {
        _defaults.For(channelId).ShouldBe(expected);
    }

    [Fact]
    public void For_NothingConfigured_ResolvesNothing()
    {
        new AgentDefaults().For("telegram").ShouldBeNull();
    }

    [Fact]
    public void Validate_DefaultNamesAnUnconfiguredAgent_Throws()
    {
        var ex = Should.Throw<InvalidOperationException>(() => _defaults.Validate(["jack", "jonas"]));

        ex.Message.ShouldContain("nabu");
    }

    [Fact]
    public void Validate_EveryDefaultNamesAConfiguredAgent_Passes()
    {
        Should.NotThrow(() => _defaults.Validate(["jack", "jonas", "nabu"]));
    }
}