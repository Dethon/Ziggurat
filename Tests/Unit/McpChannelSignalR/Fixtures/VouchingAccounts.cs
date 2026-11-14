using Domain.Contracts;
using Domain.DTOs.Channel;

namespace Tests.Unit.McpChannelSignalR.Fixtures;

// The accounts service as a hub test needs it: every session the hub asks about is live and its
// person somebody of the household, answered under their id with an assertion signed by the accounts
// test key, so a turn the hub sends carries one the agent would believe.
public sealed class VouchingAccounts(string tenantId = "home") : ISessionAccounts
{
    public Task<ResolvedAccount?> SessionAsync(string userId, string sessionId, CancellationToken ct)
    {
        var account = new Account(tenantId, userId);
        return Task.FromResult<ResolvedAccount?>(
            new ResolvedAccount(account, userId, TestAssertions.Of(account, ChannelIds.SignalR, sessionId: sessionId)));
    }
}