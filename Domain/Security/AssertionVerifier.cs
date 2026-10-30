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
        Func<CancellationToken, Task<string>> fetch, TimeProvider clock, ILoggerFactory? loggers = null) =>
        new(new JwksCache(fetch, clock, logger: loggers?.CreateLogger<JwksCache>()), clock,
            logger: loggers?.CreateLogger<AssertionVerifier>());

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
}