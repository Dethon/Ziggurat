using Domain.DTOs.Channel;
using Domain.Security;
using Mcp.Hosting;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Shouldly;

namespace Tests.Integration.McpServers;

// A dual-role server can raise something with the agent unprompted, but it cannot carry a reply back
// to a person: a schedule fires and a download finishes with nobody on the other end to speak to.
// It says so as an argument, and the channel-server call supplies the two no-op protocol tools.
//
// Opt-in, deliberately. A default-unless-overridden rule would let a real channel that forgot its
// reply tool silently drop every reply, and at registration time nothing can tell "deliberately
// absent" from "forgotten".
public class NoOutboundSurfaceTests
{
    private sealed record ProbeSettings(string Name) : IMcpHostSettings
    {
        public McpGateSettings Mcp => McpTestSecret.Gate;
    }

    [Fact]
    public void AChannelServerWithoutTheArgument_GetsNoStubs() =>
        ToolNames(services => services
            .AddMcpHost(new ProbeSettings("probe"))
            .AddChannelServer(DeliveryPolicy.Broadcast))
            .ShouldNotContain(ChannelProtocol.SendReplyTool);

    [Fact]
    public void AChannelServerDeclaringNoOutboundSurface_AdvertisesTheProtocolTools() =>
        ToolNames(services => services
            .AddMcpHost(new ProbeSettings("probe"))
            .AddChannelServer(DeliveryPolicy.Broadcast, noOutboundSurface: true))
            .ShouldBe(
                [ChannelProtocol.ReceiveTool, ChannelProtocol.SendReplyTool, ChannelProtocol.RequestApprovalTool],
                ignoreOrder: true);

    [Fact]
    public async Task AReplyChunk_IsAcceptedAndDropped()
    {
        await using var server = await StartAsync();

        var result = await server.Client.CallToolAsync(
            ChannelProtocol.SendReplyTool,
            new Dictionary<string, object?>
            {
                ["conversationId"] = "conv-1",
                ["content"] = "nobody will read this",
                ["contentType"] = "text",
                ["isComplete"] = true,
                ["messageId"] = "m-1",
                ["turnKey"] = "turn-1",
                ["agentInitiated"] = true
            });

        result.IsError.ShouldNotBe(true);
        InMemoryMcpServer.Text(result).ShouldBe("ok");
    }

    [Theory]
    [InlineData("request", "approved")]
    [InlineData("notify", "notified")]
    public async Task AnApprovalRequest_IsAnsweredWithoutAsking(string mode, string expected)
    {
        await using var server = await StartAsync();

        var result = await server.Client.CallToolAsync(
            ChannelProtocol.RequestApprovalTool,
            new Dictionary<string, object?>
            {
                ["conversationId"] = "conv-1",
                ["mode"] = mode,
                ["requests"] = Array.Empty<object>()
            });

        InMemoryMcpServer.Text(result).ShouldBe(expected);
    }

    private static Task<RunningServer> StartAsync() =>
        InMemoryMcpServer.StartAsync(services => services
            .AddMcpHost(new ProbeSettings("probe"))
            .AddChannelServer(DeliveryPolicy.Broadcast, noOutboundSurface: true));

    private static IReadOnlyList<string> ToolNames(Action<IServiceCollection> configure)
    {
        var services = new ServiceCollection();
        configure(services);

        using var provider = services.BuildServiceProvider();
        return provider.GetServices<McpServerTool>().Select(tool => tool.ProtocolTool.Name).ToList();
    }
}