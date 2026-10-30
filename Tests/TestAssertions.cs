using Domain.DTOs.Channel;
using Domain.Security;
using Domain.Security.Signing;

namespace Tests;

// Assertions as the proxy's session check hands them on, signed with the accounts service's test
// key: what a test sends an app in X-Ziggurat-Assertion to be somebody, and the verifier an
// in-process app is given to believe them, with no accounts service to fetch keys from.
internal static class TestAssertions
{
    private static readonly SigningKeyStore _accounts = TestSigningKeys.Store(TestSigningKeys.Accounts);

    // Longer than any test, so a slow run never ages one out; a test about expiry dates its own.
    private static readonly TimeSpan _lifetime = TimeSpan.FromHours(12);

    // A session id makes it the session check's, which names the session it checked.
    public static string Of(
        Account person, string channel, IReadOnlyList<string>? roles = null, DateTimeOffset? now = null,
        TimeSpan? lifetime = null, string? sessionId = null) =>
        new TokenSigner(_accounts).Sign((IdentityClaims.Person(person.UserId, person.TenantId, channel, roles ?? ["member"])
                with
        { SessionId = sessionId })
            .Stamped(TokenIssuers.Accounts, TokenAudiences.Assertion, now ?? DateTimeOffset.UtcNow, lifetime ?? _lifetime));

    public static string OfGuest(string tenantId, string channel) =>
        new TokenSigner(_accounts).Sign(IdentityClaims.Guest(tenantId, channel)
            .Stamped(TokenIssuers.Accounts, TokenAudiences.Assertion, DateTimeOffset.UtcNow, _lifetime));

    // What the accounts service never signs: an assertion naming nobody, which vouches for nothing.
    public static string OfNobody(string channel) =>
        new TokenSigner(_accounts).Sign(IdentityClaims.Nobody(channel)
            .Stamped(TokenIssuers.Accounts, TokenAudiences.Assertion, DateTimeOffset.UtcNow, _lifetime));

    // The accounts test key's published half, for a test that watches the verifier ask for it.
    public static IVerificationKeys Keys => _accounts;

    // The same claims signed by a key the accounts service does not publish.
    public static string Forged(Account person, string channel) =>
        new TokenSigner(TestSigningKeys.Store(TestSigningKeys.Agent)).Sign(
            IdentityClaims.Person(person.UserId, person.TenantId, channel, ["member"])
                .Stamped(TokenIssuers.Accounts, TokenAudiences.Assertion, DateTimeOffset.UtcNow, _lifetime));

    public static AssertionVerifier Verifier(TimeProvider? clock = null) => new(_accounts, clock ?? TimeProvider.System);
}