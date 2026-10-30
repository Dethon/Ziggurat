using Accounts.Sessions;
using Accounts.Settings;
using Accounts.Store;
using Domain.Security.Signing;

namespace Accounts.Signing;

// Signs who a turn is for, wherever this service learns the person: the session check for a browser
// behind the proxy, the internal resolution API for a channel server. Each names the channel it was
// asked for, which the agent holds against the channel the turn then comes on.
public sealed class Assertions(SigningKeyStore keys, AccountsSettings settings, AccountsClock clock)
{
    private readonly TokenSigner _signer = new(keys);

    public string Of(User person, string channel) =>
        Sign(IdentityClaims.Person(person.Id, person.TenantId, channel, Roles.Of(person)));

    public string Of(string userId, string tenantId, IReadOnlyList<string> roles, string channel) =>
        Sign(IdentityClaims.Person(userId, tenantId, channel, roles));

    // Somebody in the household who is nobody's account, vouched for as exactly that.
    public string OfGuest(string tenantId, string channel) => Sign(IdentityClaims.Guest(tenantId, channel));

    private string Sign(IdentityClaims identity) =>
        _signer.Sign(identity.Stamped(
            TokenIssuers.Accounts, TokenAudiences.Assertion, clock.Time.GetUtcNow(),
            settings.AssertionLifetime));
}