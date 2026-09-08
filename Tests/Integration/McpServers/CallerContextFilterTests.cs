using System.ComponentModel;
using System.Text.Json;
using Domain.Channels;
using Domain.DTOs.Channel;
using Mcp.Hosting;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;
using Shouldly;

namespace Tests.Integration.McpServers;

// A filesystem backend answers a tool call without ever seeing the request, so the one filter every
// server installs enters the call's conversation context for its duration. The Home Assistant mount
// reads it to record who created a watch; a call that carries none leaves it null.
public class CallerContextFilterTests
{
    private sealed record ProbeSettings(string Name);

    [Fact]
    public async Task ACallCarryingAConversationContext_SeesItAsTheCaller()
    {
        await using var server = await InMemoryMcpServer.StartAsync(services => services
            .AddToolServer(new ProbeSettings("probe"))
            .WithTools<CallerEchoTools>());
        var context = new ConversationContext("jonas", "conv-1", "fran", new ReplyTarget("telegram", "conv-1"));
        var tool = (await server.Client.ListToolsAsync()).Single(t => t.Name == "who_calls")
            .WithMeta(new System.Text.Json.Nodes.JsonObject
            {
                [ChannelProtocol.ConversationContextMetaKey] = JsonSerializer.SerializeToNode(context, ChannelProtocol.SerializerOptions)
            });

        var result = await tool.InvokeAsync(new AIFunctionArguments(), CancellationToken.None);

        var text = result is string s ? s : JsonSerializer.Serialize(result);
        text.ShouldContain("jonas:conv-1");
    }

    [Fact]
    public async Task ACallWithoutAContext_SeesNoCaller()
    {
        await using var server = await InMemoryMcpServer.StartAsync(services => services
            .AddToolServer(new ProbeSettings("probe"))
            .WithTools<CallerEchoTools>());

        var result = await server.Client.CallToolAsync("who_calls");

        InMemoryMcpServer.Text(result).ShouldBe("nobody");
    }

    // A context that does not parse is answered in the standard error envelope every other failure
    // gets — a code the model can act on — rather than escaping the filter for the SDK to wrap in
    // its own words.
    [Fact]
    public async Task ACallWhoseContextDoesNotParse_IsTheStandardErrorEnvelope()
    {
        await using var server = await InMemoryMcpServer.StartAsync(services => services
            .AddToolServer(new ProbeSettings("probe"))
            .WithTools<CallerEchoTools>());

        var result = await server.Client.CallToolAsync(new ModelContextProtocol.Protocol.CallToolRequestParams
        {
            Name = "who_calls",
            Meta = new System.Text.Json.Nodes.JsonObject { [ChannelProtocol.ConversationContextMetaKey] = "not a context" }
        });

        result.IsError.ShouldBe(true);
        var envelope = System.Text.Json.Nodes.JsonNode.Parse(InMemoryMcpServer.Text(result))!;
        envelope["errorCode"]!.GetValue<string>().ShouldBe(global::Domain.Tools.ToolError.Codes.InternalError);
        CallerContext.Current.ShouldBeNull();
    }

    [Fact]
    public void OutsideAnyCall_TheCallerIsNull()
    {
        CallerContext.Current.ShouldBeNull();
    }
}

[McpServerToolType]
public sealed class CallerEchoTools
{
    [McpServerTool(Name = "who_calls")]
    [Description("Answers the caller the filter entered, as agent:conversation.")]
    public static string WhoCalls() =>
        CallerContext.Current is { } caller ? $"{caller.AgentId}:{caller.ConversationId}" : "nobody";
}