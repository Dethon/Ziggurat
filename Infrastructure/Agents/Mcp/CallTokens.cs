using System.Collections.Concurrent;
using Domain.Security;
using Domain.Security.Signing;

namespace Infrastructure.Agents.Mcp;

// How long the agent's call tokens live and how early a fresh one is minted. Generic tunables, so
// appsettings.json alone (CallTokens:…).
public sealed record CallTokenSettings
{
    // Long enough to cover the hop to the server, short enough that one taken off the wire is worth
    // little: a server checks a token when the call arrives and never again during it.
    public TimeSpan Lifetime { get; init; } = TimeSpan.FromMinutes(2);

    // A token this close to its expiry is not presented again; a fresh one is minted instead. It
    // covers the call's transit and the clock drift between containers, so a token presented is never
    // one the server reads as expired.
    public TimeSpan RefreshMargin { get; init; } = TimeSpan.FromSeconds(30);
}

// How long a login handoff's delegation lives. A schedule's or a watch's lives as long as the object
// (Delegation.ObjectLifetime); a handoff's object is a tab that closes within the browse server's
// claim and view windows (forty minutes as shipped), and its delegation outliving that by much would
// only let a misbehaving browse server keep waking the conversation as the person.
public sealed record DelegationSettings
{
    public TimeSpan HandoffLifetime { get; init; } = TimeSpan.FromHours(1);
}

// The agent's call tokens: who a call acts for, signed for the one server it is sent to. One token
// per identity per server, reused until it nears its expiry and then minted afresh, so a turn of any
// length never presents an expired token and a reply streamed in hundreds of send_reply calls pays
// for one signature, not hundreds.
//
// Kept per identity rather than per turn: two turns acting for the same person present the same
// token to the same server, which is what one turn would have presented twice. The cache holds one
// entry per identity and server and replaces it in place, so it grows with the household, not with
// the calls.
public sealed class CallTokens(
    SigningKeyStore keys, TimeProvider clock, CallTokenSettings? settings = null, DelegationSettings? delegations = null)
{
    private readonly CallTokenSettings _settings = settings ?? new CallTokenSettings();
    private readonly DelegationSettings _delegations = delegations ?? new DelegationSettings();
    private readonly TokenSigner _signer = new(keys);
    private readonly ConcurrentDictionary<string, Minted> _minted = new(StringComparer.Ordinal);

    public string For(IdentityClaims identity, string audience)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentException.ThrowIfNullOrEmpty(audience);

        var now = clock.GetUtcNow();
        var key = string.Join('\n', audience, identity.Subject, identity.TenantId, identity.ChannelId,
            string.Join(',', identity.Roles));
        if (_minted.TryGetValue(key, out var minted) && minted.ExpiresAt - now > _settings.RefreshMargin)
        {
            return minted.Token;
        }

        var claims = identity.Stamped(TokenIssuers.Agent, audience, now, _settings.Lifetime);
        var fresh = new Minted(_signer.Sign(claims), claims.ExpiresAt);
        _minted[key] = fresh;
        return fresh.Token;
    }

    // A delegation for one write, minted afresh every time: it names the object and what the write
    // makes it do, so no two writes share one, and it is signed with the same key as the call it
    // rides on, since only the agent's own word can say a deferred turn acts for somebody.
    public string Delegate(IdentityClaims person, DelegatedObject what)
    {
        ArgumentNullException.ThrowIfNull(person);
        ArgumentNullException.ThrowIfNull(what);

        var lifetime = what.Kind == DelegatedObject.Login ? _delegations.HandoffLifetime : Delegation.ObjectLifetime;
        var delegation = (Delegation)new Delegation(person, what)
            .Stamped(TokenIssuers.Agent, TokenAudiences.Delegation, clock.GetUtcNow(), lifetime);
        return _signer.Sign(delegation);
    }

    private sealed record Minted(string Token, DateTimeOffset ExpiresAt);
}