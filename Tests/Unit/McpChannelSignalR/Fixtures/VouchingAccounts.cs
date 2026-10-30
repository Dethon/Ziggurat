using Domain.Contracts;
using Domain.DTOs.Channel;

namespace Tests.Unit.McpChannelSignalR.Fixtures;

// The accounts service as a hub test needs it: whoever the hub asks about is somebody of the
// household, answered under their id with an assertion signed by the accounts test key, so a turn
// the hub sends carries one the agent would believe.
public sealed class VouchingAccounts(string tenantId = "home") : IAccountResolver
{
    public Task<ResolvedAccount?> ResolveAsync(string channel, string identity, CancellationToken ct)
    {
        var account = new Account(tenantId, identity);
        return Task.FromResult<ResolvedAccount?>(
            new ResolvedAccount(account, identity, TestAssertions.Of(account, channel)));
    }

    public Task<ResolvedGuest> GuestAsync(string channel, CancellationToken ct) =>
        throw new InvalidOperationException("the chat hub has no guest");
}