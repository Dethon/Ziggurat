using Domain.Security;
using Microsoft.AspNetCore.SignalR;

namespace McpChannelSignalR.Hubs;

// Reaching the hub is not being somebody. A connection is the person the proxy's session check
// named in the headers it copied onto the request, registered here as it arrives; one that arrives
// without them did not come through the check, and is closed with the reason before it can call
// anything. Nothing the browser says afterwards changes who it is.
//
// Every invocation is held to the same rule here as well, in front of every method rather than at
// the top of each, so no method ever runs for a connection the check did not name — whatever a
// future change to connecting does — and a method added to the hub needs nothing to be covered.
internal sealed class RegisteredConnectionFilter : IHubFilter
{
    private const string NotSignedInError = "This connection is not signed in.";

    public Task OnConnectedAsync(HubLifetimeContext context, Func<HubLifetimeContext, Task> next)
    {
        var headers = context.Context.GetHttpContext()?.Request.Headers;
        if (headers is null || ForwardedAccount.From(name => headers[name].ToString()) is not { } account)
        {
            throw new HubException(NotSignedInError);
        }

        context.Context.Items[ChatHub.AccountItem] = account;
        return next(context);
    }

    public ValueTask<object?> InvokeMethodAsync(
        HubInvocationContext invocationContext,
        Func<HubInvocationContext, ValueTask<object?>> next) =>
        invocationContext.Context.Items.ContainsKey(ChatHub.AccountItem)
            ? next(invocationContext)
            : throw new HubException(NotSignedInError);
}