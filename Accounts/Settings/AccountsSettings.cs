using Domain.Security.Signing;
using Microsoft.Extensions.Configuration.EnvironmentVariables;
using Microsoft.Extensions.Configuration.Json;

namespace Accounts.Settings;

public record AccountsSettings
{
    // The SQLite file, in the service's own volume. Its directory is created on first start.
    public string DatabasePath { get; init; } = "/data/accounts.db";

    // Where the deployment's people reach this service: the links the operator's command prints and
    // the sign-in page the session check sends a signed-out browser to are built on it, because the
    // page asking may be on another of the deployment's hosts.
    public string PublicBaseUrl { get; init; } = "";

    // The deployment's own origins. A passkey ceremony's page must be on one of them, and sign-in
    // returns only to one of them, so a crafted link cannot send a fresh session anywhere else.
    public string[] AllowedOrigins { get; init; } = [];

    public WebAuthnSettings WebAuthn { get; init; } = new();

    public SessionCookieSettings SessionCookie { get; init; } = new();

    public TelegramSettings Telegram { get; init; } = new();

    // The servers that may ask whose account an identity is (CALLERS__<host>), each by the
    // credential it already holds for its own /mcp, so no secret exists for this alone. An entry left
    // empty admits nobody.
    public Dictionary<string, string> Callers { get; init; } = [];

    // The deployment's Redis, where a revoked tunnel credential is announced to the relay
    // (TunnelRevocationPublisher) and a saved household configuration to its readers. Nothing is
    // stored there.
    public RedisSettings Redis { get; init; } = new();

    // Households' configuration: the master key their data keys are wrapped with.
    public HouseholdConfigSettings HouseholdConfig { get; init; } = new();

    // Where the agent publishes the keys its call tokens verify with: a shared server proves which
    // household it asks about with the call token it was handed, and the agent proves it is the agent
    // with one of its own. Per deployment (AGENTKEYSURL in compose); the default is compose's value.
    public string AgentKeysUrl { get; init; } = DefaultAgentKeysUrl;

    // How long an invite or recovery link stays redeemable.
    public int LinkLifetimeDays { get; init; } = 7;

    // The key assertions are signed with. Left empty it is generated on first start into `signing/`
    // beside the database, in the service's own volume; a test stack sets SIGNING__PRIVATEKEY instead.
    public SigningKeySettings Signing { get; init; } = new();

    // An assertion only has to cover the hop from the channel that asked for it to the agent or app
    // that verifies it, which holds the verified identity from then on.
    public TimeSpan AssertionLifetime { get; init; } = TimeSpan.FromMinutes(10);

    // How every verifier of the service's assertions treats time (Domain/token-verification.json, under
    // mcp:tokenVerification). The service verifies none itself; what it needs is the skew they forgive,
    // which a retired key's last assertion must be past.
    public VerifierPolicy Mcp { get; init; } = new();

    public const string DefaultAgentKeysUrl = "http://agent:8080/internal/jwks";

    public Uri PublicBase => new(PublicBaseUrl.TrimEnd('/') + "/");

    // The service's volume: the database's directory, which its keys sit beside.
    public string DataDirectory => Path.GetDirectoryName(Path.GetFullPath(DatabasePath)) ?? ".";

    public SigningKeySettings SigningKeys =>
        Signing.PrivateKey.Length > 0 || Signing.KeyDirectory.Length > 0
            ? Signing
            : Signing with { KeyDirectory = Path.Combine(DataDirectory, "signing") };

    // The service's settings with the shared policy file read after its appsettings.json and before
    // the environment, as every verifying host reads it: put ahead of the environment the web host
    // already added, so a single container can still be overridden.
    public static AccountsSettings Read(IConfigurationBuilder configBuilder)
    {
        var policy = new JsonConfigurationSource { Path = TokenVerificationSettings.FileName, Optional = true };
        policy.ResolveFileProvider();
        var environment = configBuilder.Sources.ToList().FindIndex(source => source is EnvironmentVariablesConfigurationSource);
        if (environment < 0)
        {
            configBuilder.Add(policy);
        }
        else
        {
            configBuilder.Sources.Insert(environment, policy);
        }

        return configBuilder.Build().Get<AccountsSettings>() ?? new AccountsSettings();
    }

    // A deployment missing any of these would mint links nobody can open or ceremonies no browser
    // accepts, so it refuses to start instead.
    public IReadOnlyList<string> Missing() =>
    [
        .. new (string Name, bool Absent)[]
        {
            (nameof(PublicBaseUrl), !Uri.TryCreate(PublicBaseUrl, UriKind.Absolute, out _)),
            (nameof(AllowedOrigins), AllowedOrigins.Length == 0),
            ($"{nameof(WebAuthn)}:{nameof(WebAuthnSettings.RelyingPartyId)}", WebAuthn.RelyingPartyId.Length == 0)
        }.Where(s => s.Absent).Select(s => s.Name)
    ];
}

public record VerifierPolicy
{
    public TokenVerificationSettings TokenVerification { get; init; } = new();
}

public record HouseholdConfigSettings
{
    // The deployment's master key (HOUSEHOLDCONFIG__MASTERKEY, the base64 of 32 random bytes): it
    // wraps each household's data key, which seals that household's secrets. Empty seals nothing — a
    // section with a secret cannot be saved and a sealed one cannot be read — which is what a stack
    // with no household secrets runs on. Losing it loses every household's secrets.
    public string MasterKey { get; init; } = "";
}

public record RedisSettings
{
    // Empty tells nobody of a revocation: a test stack with no relay, or a host run without Redis.
    public string ConnectionString { get; init; } = "";
}

public record WebAuthnSettings
{
    // The registrable domain every passkey is bound to: the deployment's parent domain, so one
    // passkey signs in on each of its hosts.
    public string RelyingPartyId { get; init; } = "";

    // What a person's authenticator shows beside the passkey.
    public string RelyingPartyName { get; init; } = "Ziggurat";
}

public record SessionCookieSettings
{
    // The deployment's parent domain, so one sign-in covers every host under it. Empty makes it a
    // host-only cookie, which is what a localhost run needs: a browser refuses a parent domain the
    // page is not under.
    public string Domain { get; init; } = "";
}

public record TelegramSettings
{
    // The deployment's bot, without the @: the account page's link code and an invite are shown as
    // the bot's t.me deep link. Empty shows the bare code, for a deployment with no bot named yet.
    public string BotUsername { get; init; } = "";

    // What opening the bot with a start parameter looks like, or none when no bot is named.
    public string? DeepLink(string startParameter) =>
        BotUsername.Length == 0
            ? null
            : $"https://t.me/{Uri.EscapeDataString(BotUsername.TrimStart('@'))}?start={startParameter}";
}