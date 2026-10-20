using System.Text.Json.Nodes;
using Shouldly;
using Tests.E2E.Fixtures;

namespace Tests.Unit.E2E;

// The WebChat stack runs the sugary agent as the deployment defines it, read from the shipped
// Agent/appsettings.json while the stack starts. A file it could not read, or an agent it could not
// find, would fail every WebChat collection at start-up rather than the one test that needs it.
public class WebChatStackTests
{
    [Fact]
    public void TheShippedSugaryAgent_IsReadAsTheDeploymentDefinesIt()
    {
        var agent = JsonNode.Parse(WebChatStack.ShippedAgent("sugary"))!;

        agent["id"]!.GetValue<string>().ShouldBe("sugary");
        agent["model"]!.GetValue<string>().ShouldNotBeNullOrWhiteSpace();
        agent["mcpServerEndpoints"]!.AsArray().Select(e => e!.GetValue<string>())
            .ShouldBe(["http://mcp-sugary:8080/mcp"]);
    }
}