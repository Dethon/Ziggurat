using Infrastructure.Utils;
using Shouldly;

namespace Tests.Unit.Infrastructure;

public class ToolPatternMatcherTests
{
    [Theory]
    [InlineData("mcp__server__Tool", "mcp__server__Tool", true)]
    [InlineData("mcp__server__Tool", "mcp__server__tool", true)] // case insensitive
    [InlineData("mcp__server__Tool", "mcp__server__OtherTool", false)]
    public void IsMatch_ExactPattern_MatchesCorrectly(string toolName, string pattern, bool expected)
    {
        var matcher = new ToolPatternMatcher([pattern]);
        matcher.IsMatch(toolName).ShouldBe(expected);
    }

    [Theory]
    [InlineData("mcp__any-server__AnyTool", "mcp__*", true)]
    [InlineData("mcp__localhost__TestTool", "mcp__*", true)]
    [InlineData("local__SomeTool", "mcp__*", false)]
    public void IsMatch_AllMcpWildcard_MatchesAllMcpTools(string toolName, string pattern, bool expected)
    {
        var matcher = new ToolPatternMatcher([pattern]);
        matcher.IsMatch(toolName).ShouldBe(expected);
    }

    [Fact]
    public void IsMatch_MultiplePatterns_MatchesAny()
    {
        var matcher = new ToolPatternMatcher(["mcp__server1__*", "mcp__server2__SpecificTool"]);

        matcher.IsMatch("mcp__server1__AnyTool").ShouldBeTrue();
        matcher.IsMatch("mcp__server2__SpecificTool").ShouldBeTrue();
        matcher.IsMatch("mcp__server2__OtherTool").ShouldBeFalse();
        matcher.IsMatch("mcp__server3__Tool").ShouldBeFalse();
    }

    [Fact]
    public void IsMatch_NullPatterns_MatchesNothing()
    {
        var matcher = new ToolPatternMatcher(null);
        matcher.IsMatch("mcp__server__Tool").ShouldBeFalse();
    }

    [Theory]
    [InlineData("mcp__mcp-library__FileSearch", "*__FileSearch", true)]
    [InlineData("mcp__other-server__FileSearch", "*__FileSearch", true)]
    [InlineData("local__FileSearch", "*__FileSearch", true)]
    [InlineData("mcp__server__OtherTool", "*__FileSearch", false)]
    public void IsMatch_ToolNameWildcard_MatchesToolFromAnySource(string toolName, string pattern, bool expected)
    {
        var matcher = new ToolPatternMatcher([pattern]);
        matcher.IsMatch(toolName).ShouldBe(expected);
    }
}