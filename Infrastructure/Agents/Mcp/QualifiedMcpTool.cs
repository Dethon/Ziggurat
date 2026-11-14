using System.Text.Json;
using System.Text.Json.Nodes;
using Domain.Contracts;
using Domain.Tools;
using Microsoft.Extensions.AI;
using ModelContextProtocol;
using ModelContextProtocol.Client;

namespace Infrastructure.Agents.Mcp;

// The store is constructor-injected and nullable: a host without one passes an image result
// through exactly as before rather than failing, the same bargain IAttachmentSource makes.
//
// `offline` is cancelled when the server's tunnel closes (McpServerEndpoint.Offline); a server not on
// a tunnel never goes offline this way and passes none. `reconnected` says whether that server has
// opened a newer tunnel since (McpServerEndpoint.Reconnected).
internal sealed class QualifiedMcpTool(
    string serverName,
    McpClientTool innerTool,
    IReadImageStore? readImageStore = null,
    CancellationToken offline = default,
    Func<bool>? reconnected = null) : AIFunction
{
    private const string McpPrefix = "mcp";
    private const string Separator = "__";

    public override string Name { get; } = $"{McpPrefix}{Separator}{serverName}{Separator}{innerTool.Name}";

    // What the server said a call to this tool touches, for whoever orders the calls of one
    // message; null where it said nothing.
    private readonly FootprintDeclaration? _footprint = McpFootprint.Of(serverName, innerTool.ProtocolTool);

    public override string Description => innerTool.Description;
    public override JsonElement JsonSchema => innerTool.JsonSchema;

    public override object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceKey is null && serviceType == typeof(FootprintDeclaration)
            ? _footprint
            : base.GetService(serviceType, serviceKey);

    public QualifiedMcpTool WithProgress(IProgress<ProgressNotificationValue> progress)
    {
        return new QualifiedMcpTool(serverName, innerTool.WithProgress(progress), readImageStore, offline, reconnected);
    }

    protected override async ValueTask<object?> InvokeCoreAsync(
        AIFunctionArguments arguments,
        CancellationToken cancellationToken)
    {
        var context = ConversationContextMeta.TryRead(FunctionInvokingChatClient.CurrentContext?.Options);
        var meta = ConversationContextMeta.TryBuild(FunctionInvokingChatClient.CurrentContext?.Options);
        var tool = meta is null ? innerTool : innerTool.WithMeta(meta);
        if (offline.IsCancellationRequested)
        {
            return NotSent();
        }

        // The call waits on the tunnel as well as on the turn: a tunnel that closes under a call ends
        // it whether or not anything the transport reads notices first.
        object? result;
        using var call = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, offline);
        try
        {
            result = await tool.InvokeAsync(arguments, call.Token);
        }
        catch (Exception) when (offline.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            return WentOffline();
        }

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

    // What the stream's IOException or the client's refusal would have said, in the words the model
    // acts on: the server is not answering, nothing it did is known to have landed, and it is the
    // user's machine to switch back on. Its tools stay listed for the rest of this turn and give the
    // same answer; the next turn goes without them and names the server as offline.
    private JsonObject WentOffline() =>
        CapabilityError.For(
            CapabilityState.Unavailable,
            $"{serverName} went offline: its tunnel closed while the call was in flight, so whether it did anything is unknown",
            $"Tell the user {serverName} is offline. Its tools come back on a later turn once it reconnects; "
            + "calling them again this turn gets the same answer. " + CannotRetry).ToNode();

    // The tunnel had closed before the call: nothing was sent. A turn that waited — on an approval,
    // say — across the server reconnecting finds it back, on a connection this turn cannot use (its
    // tools are its opening's, ADR 0012), and is told so: Jonas otherwise told the person the server
    // was down and promised to retry by itself (pass 19, 0 of 7 replies right before, 12 of 12 after).
    private JsonObject NotSent() =>
        reconnected?.Invoke() == true
            ? CapabilityError.For(
                CapabilityState.Unavailable,
                $"{serverName} reconnected after this turn began, on a new connection this turn cannot use, so this call was not sent and nothing was done",
                $"Tell the user this was not done because {serverName} reconnected meanwhile, and that it works if they ask again. "
                + CannotRetry).ToNode()
            : CapabilityError.For(
                CapabilityState.Unavailable,
                $"{serverName} is offline: its tunnel closed before this call, so the call was not sent and nothing was done",
                $"Tell the user {serverName} is offline and this was not done. Its tools come back on a later turn once it reconnects; "
                + "calling them again this turn gets the same answer. " + CannotRetry).ToNode();

    private const string CannotRetry = "You cannot retry it yourself after this reply, so do not promise to.";

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