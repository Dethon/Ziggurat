using System.ComponentModel;
using System.Reflection;
using Domain.Channels;
using Domain.DTOs;
using Domain.DTOs.Channel;
using Infrastructure.Agents.Mcp;
using Infrastructure.Clients.Channels;
using Mcp.Hosting;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Shouldly;
using Tests.Integration.McpServers;

namespace Tests.Integration.Channels;

// An approval is asked of the channel by the agent host, not by the model, so it is a hop that
// used to carry no `_meta`. The channel that reads a spoken answer has to know what the turn
// asked for — a turn addressed to the local box is never put to a hosted judge — and the only
// place that rides is the turn's conversation context.
public class McpChannelConnectionApprovalMetaTests
{
    private sealed record ProbeSettings(string Name);

    private static readonly PropertyInfo _currentContext =
        typeof(FunctionInvokingChatClient).GetProperty("CurrentContext", BindingFlags.Public | BindingFlags.Static)!;

    [Fact]
    public async Task AnApprovalAskedInsideATurn_CarriesTheTurnsContextToTheChannel()
    {
        await using var server = await InMemoryMcpServer.StartAsync(services => services
            .AddToolServer(new ProbeSettings("probe"))
            .WithTools<ModelEchoingApprovalTools>());
        await using var connection = new McpChannelConnection("test");
        await connection.ConnectAsync(server.Endpoint, CancellationToken.None);
        var context = new ConversationContext(
            "jack", "conv-1", "fran", new ReplyTarget("signalr", "conv-1"), "lemonade/qwen3");
        _currentContext.SetValue(null, new FunctionInvocationContext
        {
            Options = new ChatOptions
            {
                AdditionalProperties = new AdditionalPropertiesDictionary { [ConversationContextMeta.OptionsKey] = context }
            }
        });

        try
        {
            var result = await connection.RequestApprovalAsync(
                "conv-1", [new ToolApprovalRequest(null, "mcp__home__call", new Dictionary<string, object?>())], CancellationToken.None);

            result.ShouldBe(ToolApprovalResult.Approved);
        }
        finally
        {
            _currentContext.SetValue(null, null);
        }
    }

    // Approves only when the call's `_meta` names a turn that asked for the local box, so the
    // test reads what the server saw through the result the connection always returns.
    [McpServerToolType]
    public sealed class ModelEchoingApprovalTools
    {
        [McpServerTool(Name = ChannelProtocol.RequestApprovalTool)]
        [Description("Approves when the caller's turn asked for a Lemonade model.")]
        public static string RequestApproval(
            string conversationId, ApprovalMode mode, IReadOnlyList<ToolApprovalRequest> requests,
            RequestContext<CallToolRequestParams> context) =>
            ConversationScope.Parse(context.Params?.Meta)?.ConfigPatchModel == "lemonade/qwen3" ? "approved" : "rejected";
    }
}