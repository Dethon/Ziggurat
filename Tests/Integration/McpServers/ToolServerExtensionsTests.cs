using Mcp.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ModelContextProtocol.AspNetCore;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Shouldly;

namespace Tests.Integration.McpServers;

// The other half of Mcp.Hosting, beside AddChannelServer: the MCP host every server has, and the
// tool server that is the host plus the error rule. Being a tool server and being a channel server
// are independent facts about a server, which is why they are two calls rather than one with a
// flag — the dual-role servers are genuinely both.
public class ToolServerExtensionsTests
{
    private sealed record ProbeSettings(string Name);

    // The host on its own carries no error rule: a server that offers the agent nothing to call has
    // nothing to map an exception for.
    [Fact]
    public void AddMcpHost_AddsNoCallToolFilter() =>
        McpServerProbe.CallToolFilterCount(services => services.AddMcpHost(new ProbeSettings("probe")))
            .ShouldBe(0);

    [Fact]
    public void AddToolServer_IsTheHostPlusTheFilter() =>
        McpServerProbe.CallToolFilterCount(services => services.AddToolServer(new ProbeSettings("probe")))
            .ShouldBe(1);

    // A dual-role server asks as a tool server and again as a channel server, and ends up with one
    // filter — the count is asserted over the two real dual-role servers by
    // McpServerContractTests.EveryServer_HasExactlyOneCallToolFilter. What is left to pin here is
    // the ordering, over the wire: the tool-server call comes first on both real ones, so its error
    // shape is the one a caller sees.
    [Fact]
    public async Task ADualRoleServer_AnswersWithTheShapeTheFirstCallPassed()
    {
        await using var server = await InMemoryMcpServer.StartAsync(services => services
            .AddToolServer(new ProbeSettings("probe"), Marked("tool-server"))
            .WithTools<FailingTools>()
            .AddChannelServer(DeliveryPolicy.Broadcast, errorResult: Marked("channel-server")));

        var result = await server.Client.CallToolAsync("throws");

        InMemoryMcpServer.Text(result).ShouldContain("tool-server");
    }

    private static Func<Exception, CallToolResult> Marked(string marker) => ex => new CallToolResult
    {
        IsError = true,
        Content = [new TextContentBlock { Text = $"{marker}: {ex.Message}" }]
    };
}