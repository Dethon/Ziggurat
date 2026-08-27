using System.Text.Json;
using Domain.Contracts;
using Microsoft.Extensions.AI;
using ModelContextProtocol;
using ModelContextProtocol.Client;

namespace Infrastructure.Agents.Mcp;

// The store is constructor-injected and nullable: a host without one passes an image result
// through exactly as before rather than failing, the same bargain IAttachmentSource makes.
internal sealed class QualifiedMcpTool(
    string serverName,
    McpClientTool innerTool,
    IReadImageStore? readImageStore = null) : AIFunction
{
    private const string McpPrefix = "mcp";
    private const string Separator = "__";

    public override string Name { get; } = $"{McpPrefix}{Separator}{serverName}{Separator}{innerTool.Name}";

    public override string Description => innerTool.Description;
    public override JsonElement JsonSchema => innerTool.JsonSchema;

    public QualifiedMcpTool WithProgress(IProgress<ProgressNotificationValue> progress)
    {
        return new QualifiedMcpTool(serverName, innerTool.WithProgress(progress), readImageStore);
    }

    protected override async ValueTask<object?> InvokeCoreAsync(
        AIFunctionArguments arguments,
        CancellationToken cancellationToken)
    {
        var context = ConversationContextMeta.TryRead(FunctionInvokingChatClient.CurrentContext?.Options);
        var meta = ConversationContextMeta.TryBuild(FunctionInvokingChatClient.CurrentContext?.Options);
        var tool = meta is null ? innerTool : innerTool.WithMeta(meta);
        var result = await tool.InvokeAsync(arguments, cancellationToken);

        // Lifted here rather than inside Flatten: this is where the conversation is already
        // resolved, and Flatten is a pure shape rule with no access to ids or storage. Bytes must
        // not survive past this line -- everything downstream serializes a turn into the history.
        var lifted = await McpImageLift.ApplyAsync(
            result,
            readImageStore,
            context?.ConversationId,
            FunctionInvokingChatClient.CurrentContext?.CallContent.CallId,
            cancellationToken);

        return Flatten(lifted);
    }

    // Multi-block tool results from MCP arrive here as AIContent[]. The downstream
    // OpenAI bridge (Microsoft.Extensions.AI.OpenAI) JSON-serializes any non-string
    // FunctionResultContent.Result into the tool message, which re-escapes every
    // body character. Flattening to a single string short-circuits that path.
    internal static object? Flatten(object? result)
    {
        if (result is not IList<AIContent> contents || contents.Count <= 1)
        {
            return result;
        }

        if (!contents.All(c => c is TextContent))
        {
            return result;
        }

        return string.Join("\n\n", contents.OfType<TextContent>().Select(c => c.Text));
    }
}