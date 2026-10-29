using Domain.DTOs.WebChat;
using Microsoft.AspNetCore.SignalR;

namespace McpChannelSignalR.Hubs;

// Reaching the hub is not being somebody: every call but registration (and the liveness probe,
// which reads nothing) is refused until the connection has said who it is. Held here, in front of
// every invocation, rather than at the top of each method, so a method added to the hub is refused
// by default instead of open until somebody remembers to guard it.
//
// Each refusal is in the shape the caller already handles from that method. A stream ends on one
// error chunk, the way a turn that failed ends; EnqueueMessage answers false, which the client
// reads as "no stream to enqueue onto" and sends on a stream of its own, where the refusal is then
// said; everything else throws.
internal sealed class RegisteredConnectionFilter : IHubFilter
{
    private const string NotRegisteredError = "User not registered. Call RegisterUser first.";

    private static readonly HashSet<string> _openToAnyone = [nameof(ChatHub.RegisterUser), nameof(ChatHub.Ping)];

    public ValueTask<object?> InvokeMethodAsync(
        HubInvocationContext invocationContext,
        Func<HubInvocationContext, ValueTask<object?>> next)
    {
        if (_openToAnyone.Contains(invocationContext.HubMethodName)
            || invocationContext.Context.Items.ContainsKey(ChatHub.UserIdItem))
        {
            return next(invocationContext);
        }

        if (invocationContext.HubMethod.ReturnType == typeof(IAsyncEnumerable<ChatStreamMessage>))
        {
            return ValueTask.FromResult<object?>(RefusedStream());
        }

        if (invocationContext.HubMethodName == nameof(ChatHub.EnqueueMessage))
        {
            return ValueTask.FromResult<object?>(false);
        }

        throw new HubException(NotRegisteredError);
    }

    private static IAsyncEnumerable<ChatStreamMessage> RefusedStream() =>
        new[] { new ChatStreamMessage { Error = NotRegisteredError, IsComplete = true } }.ToAsyncEnumerable();
}