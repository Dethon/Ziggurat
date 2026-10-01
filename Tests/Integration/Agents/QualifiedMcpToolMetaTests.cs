using System.Reflection;
using System.Text.Json;
using Domain.DTOs.Channel;
using Infrastructure.Agents.Mcp;
using Microsoft.Extensions.AI;
using ModelContextProtocol.Client;
using Shouldly;
using Tests.Integration.Fixtures;

namespace Tests.Integration.Agents;

public class QualifiedMcpToolMetaTests(MetaEchoServerFixture fixture) : IClassFixture<MetaEchoServerFixture>
{
    private static readonly PropertyInfo _currentContextSetter =
        typeof(FunctionInvokingChatClient)
            .GetProperty("CurrentContext", BindingFlags.Public | BindingFlags.Static)!;

    private static void SetCurrentContext(FunctionInvocationContext? ctx)
        => _currentContextSetter.SetValue(null, ctx);

    [Fact]
    public async Task InvokeCore_WithCurrentContext_DeliversConversationContextAsMeta()
    {
        await using var client = await McpClient.CreateAsync(
            McpTestSecret.Transport(fixture.McpEndpoint));
        var tool = (await client.ListToolsAsync()).Single(t => t.Name == "echo_meta");
        var qualified = new QualifiedMcpTool("echo", tool);

        var context = new ConversationContext("jack", "conv-1", "fran", new ReplyTarget("signalr", "conv-1"));
        SetCurrentContext(new FunctionInvocationContext
        {
            Options = new ChatOptions
            {
                AdditionalProperties = new AdditionalPropertiesDictionary
                {
                    [ConversationContextMeta.OptionsKey] = context
                }
            }
        });
        try
        {
            var result = await qualified.InvokeAsync(new AIFunctionArguments(), CancellationToken.None);
            var text = result is string s ? s : JsonSerializer.Serialize(result);
            text.ShouldContain(ChannelProtocol.ConversationContextMetaKey);
            text.ShouldContain("conv-1");
            text.ShouldContain("signalr");
        }
        finally
        {
            SetCurrentContext(null);
        }
    }
}