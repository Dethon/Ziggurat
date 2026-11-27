using Domain.DTOs.Channel;
using Domain.Security;
using Domain.Security.Signing;

namespace Tests;

// A check's call token as the agent signs one for its tool call to the apps server, which hands it back for
// the check's reads and asks, and the verifier the agent believes it with (CheckCallVerifier): the agent's
// own test key, for the apps server at the deployment's address (whose audience is McpTestSecret.Audience).
internal static class TestCheckCalls
{
    public const string AppsServer = "http://localhost:6018/mcp";

    public static string Of(
        Account person, string audience = McpTestSecret.Audience, string? key = null, DateTimeOffset? now = null) =>
        Of(IdentityClaims.Person(person.UserId, person.TenantId, "signalr", ["member"]), audience, key, now);

    public static string Of(
        IdentityClaims identity, string audience = McpTestSecret.Audience, string? key = null, DateTimeOffset? now = null) =>
        new TokenSigner(TestSigningKeys.Store(key ?? TestSigningKeys.Agent)).Sign(
            identity.Stamped(TokenIssuers.Agent, audience, now ?? DateTimeOffset.UtcNow, TimeSpan.FromMinutes(2)));

    public static CheckCallVerifier Verifier() =>
        new(TestSigningKeys.Store(TestSigningKeys.Agent), ServerAddress.FromUrl(AppsServer), TimeProvider.System);
}