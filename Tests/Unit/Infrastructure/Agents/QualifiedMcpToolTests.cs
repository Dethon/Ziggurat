using Infrastructure.Agents.Mcp;
using Microsoft.Extensions.AI;
using Shouldly;

namespace Tests.Unit.Infrastructure.Agents;

public class QualifiedMcpToolTests
{
    [Fact]
    public void Flatten_TwoTextContents_JoinsWithBlankLineSeparator()
    {
        AIContent[] input =
        [
            new TextContent("{\"status\":\"success\"}"),
            new TextContent("# Heading\n\nBody with \"quotes\".")
        ];

        var result = QualifiedMcpTool.Flatten(input);

        result.ShouldBe("{\"status\":\"success\"}\n\n# Heading\n\nBody with \"quotes\".");
    }

    [Fact]
    public void Flatten_SingleAIContent_ReturnsUnchanged()
    {
        var single = new TextContent("only block");

        var result = QualifiedMcpTool.Flatten(single);

        result.ShouldBeSameAs(single);
    }

    [Fact]
    public void Flatten_MultiBlock_WithNonTextContent_ReturnsUnchanged()
    {
        AIContent[] input =
        [
            new TextContent("text"),
            new DataContent(new byte[] { 1, 2, 3 }, "image/png")
        ];

        var result = QualifiedMcpTool.Flatten(input);

        result.ShouldBeSameAs(input);
    }

    [Fact]
    public void Flatten_Null_ReturnsNull()
    {
        QualifiedMcpTool.Flatten(null).ShouldBeNull();
    }
}