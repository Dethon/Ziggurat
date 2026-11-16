using System.Collections.Concurrent;
using Domain.Contracts;
using Domain.DTOs.Channel;
using Domain.Security;
using Microsoft.AspNetCore.SignalR;

namespace McpChannelSignalR.Services;

// The open connections by the session each signed in with, so a device signed out elsewhere stops
// hearing its person at once. A connection's person was settled as it arrived and every send asks for
// its session again, but reads and pushes do not: a page whose session was revoked from another device
// stayed in its person's groups, was pushed their new private conversations and could open them, for
// as long as it stayed connected. Told a person's sessions ended (SessionsEnded), the hub asks the
// accounts service about each session of theirs it holds and closes the connections of the ones it no
// longer vouches for; the closed page reconnects through the proxy's session check, which refuses it.
public sealed class SessionConnections(ISessionAccounts sessions, ILogger<SessionConnections> logger)
{
    private readonly ConcurrentDictionary<string, Held> _held = new(StringComparer.Ordinal);

    // A connection that came with no session to ask about is never vouched for again anyway: its
    // turns are answered signed out (ChatHub.VouchedAsync), so there is nothing here to end.
    public void Opened(HubCallerContext connection, ForwardedAccount account)
    {
        if (account.SessionId is { } sessionId)
        {
            _held[connection.ConnectionId] = new Held(account.UserId, sessionId, connection);
        }
    }

    public void Closed(string connectionId) => _held.TryRemove(connectionId, out _);

    // Asked per session rather than believed from the announcement, so the announcement opens nothing
    // and only what the service says is over is closed. A session it cannot answer for just now is
    // left open: an outage is not a revocation, and the next send asks again.
    public async Task CloseEndedAsync(SessionsEnded ended, CancellationToken ct)
    {
        var theirs = _held.Values.Where(h => h.UserId == ended.UserId).ToList();
        var verdicts = await Task.WhenAll(theirs.Select(h => h.SessionId).Distinct(StringComparer.Ordinal)
            .Select(async sessionId => (SessionId: sessionId, Ended: await EndedAsync(ended.UserId, sessionId, ct))));
        var over = verdicts.Where(v => v.Ended).Select(v => v.SessionId).ToHashSet(StringComparer.Ordinal);
        theirs.Where(h => over.Contains(h.SessionId)).ToList().ForEach(h =>
        {
            logger.LogInformation("Closing connection {ConnectionId} of account {UserId}: its session ended",
                h.Connection.ConnectionId, h.UserId);
            _held.TryRemove(h.Connection.ConnectionId, out _);
            h.Connection.Abort();
        });
    }

    private async Task<bool> EndedAsync(string userId, string sessionId, CancellationToken ct)
    {
        try
        {
            return await sessions.SessionAsync(userId, sessionId, ct) is null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Could not ask whether a session of account {UserId} ended; its connections stay open", userId);
            return false;
        }
    }

    private sealed record Held(string UserId, string SessionId, HubCallerContext Connection);
}