using Domain.Security.Signing;
using Microsoft.Extensions.Logging;

namespace Domain.Security;

// Who an app behind the proxy believes a request is from: the person the accounts service's
// assertion names, verified against the service's published keys, or nobody. The three plain
// headers beside it are never read for this — any container on the compose network can write them,
// none of them can sign — so an assertion naming one person under headers naming another acts for
// the assertion's.
//
// Two checks beyond the token's own. The assertion must name the app's channel: each forward_auth
// block names what it guards (`?channel=`), so one signed for Sugary opens neither the chat hub nor
// the dashboard. And it must name a person: the guest is a signed claim for a satellite's turn, and
// to an app the guest is nobody.
//
// The agent holds the same verifier, over the same copy of the keys, for a second use: the turn a
// channel carries an assertion on (VerifyTurnAsync), where the guest is somebody after all.
public sealed class AssertionVerifier(
    IVerificationKeys keys,
    TimeProvider clock,
    TokenVerificationSettings? settings = null,
    ILogger<AssertionVerifier>? logger = null)
{
    // Where compose publishes the accounts service's keys (ACCOUNTS__JWKSURL), the default for every
    // app so a stack that wires nothing else still verifies.
    public const string DefaultJwksUrl = "http://accounts:8080/accounts/internal/jwks";

    private readonly TokenVerifier _verifier = new(TokenIssuers.Accounts, keys, clock, settings);

    // A host hands in the fetch over its own HttpClient: Domain makes no HTTP call.
    public static AssertionVerifier OverJwks(
        Func<CancellationToken, Task<string>> fetch, TimeProvider clock, TokenVerificationSettings settings,
        ILoggerFactory? loggers = null) =>
        new(new JwksCache(fetch, clock, settings, loggers?.CreateLogger<JwksCache>()), clock, settings,
            loggers?.CreateLogger<AssertionVerifier>());

    public async Task<ForwardedAccount?> VerifyAsync(string? assertion, string channel, CancellationToken ct)
    {
        var verification = await _verifier.VerifyAsync(assertion, TokenAudiences.Assertion, ct);
        if (!verification.IsVerified)
        {
            // Missing is an ordinary signed-out request; anything else is a forgery, a clock, or an
            // accounts service this app cannot reach (KeysUnavailable) — and then every signed-in
            // person is turned away until somebody looks.
            logger?.Log(verification.Refusal == TokenRefusal.Missing ? LogLevel.Debug : LogLevel.Warning,
                "An assertion for {Channel} was refused: {Refusal}", channel, verification.Refusal);
            return null;
        }

        var claims = verification.Claims;
        if (claims.ChannelId != channel || claims.Account is not { } account)
        {
            logger?.LogWarning("An assertion for {Channel} named channel {Named} and {Subject}; it is not believed here",
                channel, claims.ChannelId, claims.IsGuest ? "the guest" : "a person");
            return null;
        }

        return new ForwardedAccount(account.UserId, account.TenantId, claims.Roles);
    }

    // The agent's check on a person's turn as it arrives, over the same keys: the accounts service
    // signed it, for TokenAudiences.Assertion, in date, for the very channel the turn came on — and
    // for a person or the household's guest, both somebody the house works for. Never nobody: the
    // service signs no such assertion, so one that says it vouches for nothing. What it answers is the
    // identity alone, out of its envelope: the turn acts for it however long it runs, so the
    // assertion's ten minutes are spent once it has been checked.
    public async Task<TurnAdmission> VerifyTurnAsync(string? assertion, string channel, CancellationToken ct)
    {
        var verification = await _verifier.VerifyAsync(assertion, TokenAudiences.Assertion, ct);
        if (!verification.IsVerified)
        {
            return new TurnAdmission.Refused(verification.Refusal.Value.ToString());
        }

        var claims = verification.Claims;
        return claims switch
        {
            { IsNobody: true } => new TurnAdmission.Refused(TurnAdmission.NamesNobody),
            _ when claims.ChannelId != channel => new TurnAdmission.Refused(TurnAdmission.WrongChannel),
            { IsGuest: true } => new TurnAdmission.Admitted(IdentityClaims.Guest(claims.TenantId, claims.ChannelId)),
            _ => new TurnAdmission.Admitted(
                IdentityClaims.Person(claims.Subject, claims.TenantId, claims.ChannelId, claims.Roles))
        };
    }
}

// Who a person's turn acts for, as its assertion verified, or why the turn is not believed: a token
// refusal's name (TokenRefusal), or one of the two the turn adds.
public abstract record TurnAdmission
{
    public const string WrongChannel = "WrongChannel";
    public const string NamesNobody = "NamesNobody";

    public sealed record Admitted(IdentityClaims Identity) : TurnAdmission;

    public sealed record Refused(string Reason) : TurnAdmission;
}