using Domain.Contracts;
using Domain.DTOs.Channel;

namespace Tests.Unit.McpChannelVoice;

// The accounts service as the voice server sees it: who holds a voice identity, nobody, or an outage.
// Empty by default, so a dispatcher built for something else sends every speaker out as the guest.
internal sealed class FakeAccountResolver : IAccountResolver
{
    private readonly Dictionary<string, ResolvedAccount> _accounts = new(StringComparer.Ordinal);
    private Exception? _outage;

    public List<(string Channel, string Identity)> Asked { get; } = [];

    public FakeAccountResolver Holding(string userId, string tenantId, string displayName)
    {
        _accounts[userId] = new ResolvedAccount(new Account(tenantId, userId), displayName);
        return this;
    }

    public FakeAccountResolver Down(Exception? outage = null)
    {
        _outage = outage ?? new HttpRequestException("accounts unreachable");
        return this;
    }

    public Task<ResolvedAccount?> ResolveAsync(string channel, string identity, CancellationToken ct)
    {
        Asked.Add((channel, identity));
        return _outage is not null
            ? Task.FromException<ResolvedAccount?>(_outage)
            : Task.FromResult(channel == "voice" ? _accounts.GetValueOrDefault(identity) : null);
    }
}