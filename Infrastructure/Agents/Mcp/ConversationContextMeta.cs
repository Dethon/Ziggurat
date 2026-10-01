using System.Text.Json;
using System.Text.Json.Nodes;
using Domain.DTOs.Channel;
using Microsoft.Extensions.AI;

namespace Infrastructure.Agents.Mcp;

internal static class ConversationContextMeta
{
    public const string OptionsKey = "ConversationContext";
    public const string MetaKey = ChannelProtocol.ConversationContextMetaKey;

    // The context of the tool invocation currently in flight. FunctionInvokingChatClient exposes
    // the enclosing invocation's ChatOptions, which is where McpAgent stamps the turn's context --
    // so a tool that spawns work of its own (run_subagent) can hand its child the caller's context
    // verbatim, without any mutable per-agent state that concurrent turns would race on.
    public static ConversationContext? Current => TryRead(FunctionInvokingChatClient.CurrentContext?.Options);

    public static ConversationContext? TryRead(ChatOptions? options)
        => options?.AdditionalProperties?.GetValueOrDefault(OptionsKey) as ConversationContext;

    public static JsonObject? TryBuild(ChatOptions? options) => Build(TryRead(options));

    public static JsonObject? Build(ConversationContext? context) =>
        context is null
            ? null
            : new JsonObject
            {
                [MetaKey] = JsonSerializer.SerializeToNode(context, ChannelProtocol.SerializerOptions)
            };
}