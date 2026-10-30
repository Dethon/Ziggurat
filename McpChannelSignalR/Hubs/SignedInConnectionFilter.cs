using Domain.Contracts;
using Domain.DTOs.Channel;
using Domain.Security;
using McpChannelSignalR.Services;
using Microsoft.AspNetCore.SignalR;

namespace McpChannelSignalR.Hubs;

// Reaching the hub is not being somebody. A connection is the person the proxy's session check
// named in the assertion it signed and copied onto the request — verified against the accounts
// service's keys and held to the chat hub's channel — kept here as it arrives. The three plain
// headers beside it are never read: anything on the compose network could write them. One that
// arrives without a believable assertion did not come through the check, and is closed with the
// reason before it can call anything. Nothing the browser says afterwards changes who it is, and the
// assertion is not asked again as it ages: who the connection is was settled when it arrived.
//
// Every invocation is held to the same rule here as well, in front of every method rather than at
// the top of each, so no method ever runs for a connection the check did not name — whatever a
// future change to connecting does — and a method added to the hub needs nothing to be covered.
internal sealed class SignedInConnectionFilter(
    AssertionVerifier assertions,
    ISessionAccounts sessions,
    AccountConnections connections,
    ILogger<SignedInConnectionFilter> logger) : IHubFilter
{
    private const string NotSignedInError = "This connection is not signed in.";

    public async Task OnConnectedAsync(HubLifetimeContext context, Func<HubLifetimeContext, Task> next)
    {
        var headers = context.Context.GetHttpContext()?.Request.Headers ?? new HeaderDictionary();
        if (await assertions.VerifyRequestAsync(headers, ChannelIds.SignalR, context.Context.ConnectionAborted) is not { } account)
        {
            throw new HubException(NotSignedInError);
        }

        context.Context.Items[ChatHub.AccountItem] = account;
        context.Context.Items[ChatHub.DisplayNameItem] =
            await DisplayNameOfAsync(account, context.Context.ConnectionAborted);
        // A question offered to this person reaches them on every connection they hold, whatever
        // space it is in, and with none held goes to their devices instead.
        await context.Hub.Groups.AddToGroupAsync(
            context.Context.ConnectionId, TopicAudience.Account(account.UserId), context.Context.ConnectionAborted);
        await next(context);
        connections.Opened(account.UserId);
    }

    public Task OnDisconnectedAsync(
        HubLifetimeContext context, Exception? exception, Func<HubLifetimeContext, Exception?, Task> next)
    {
        if (context.Context.Items.TryGetValue(ChatHub.AccountItem, out var item) && item is ForwardedAccount account)
        {
            connections.Closed(account.UserId);
        }

        return next(context, exception);
    }

    public ValueTask<object?> InvokeMethodAsync(
        HubInvocationContext invocationContext,
        Func<HubInvocationContext, ValueTask<object?>> next) =>
        invocationContext.Context.Items.ContainsKey(ChatHub.AccountItem)
            ? next(invocationContext)
            : throw new HubException(NotSignedInError);

    // Asked once per connection, because a turn's sender is the name and the name does not change
    // mid-connection often enough to ask on every message — of the session the assertion named, the
    // one way the accounts service answers the chat hub's person. The assertion already said who this
    // is, so a name the service could not give is never a reason to refuse the person: their turns go
    // out under the account's id instead (and a session gone by now refuses each turn anyway).
    private async Task<string?> DisplayNameOfAsync(ForwardedAccount account, CancellationToken ct)
    {
        if (account.SessionId is not { } sessionId)
        {
            return null;
        }

        try
        {
            return (await sessions.SessionAsync(account.UserId, sessionId, ct))?.DisplayName;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Could not resolve the display name of account {UserId}; its turns carry the id", account.UserId);
            return null;
        }
    }
}