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

    // Where a household's keys are checked before they are saved, and how long each provider is
    // given to answer. Generic (appsettings.json): a test stack points them at a stub.
    public KeyProbeSettings KeyProbes { get; init; } = new();

    // Where the agent publishes the keys its call tokens verify with: a shared server proves which
    // household it asks about with the call token it was handed, and the agent proves it is the agent
    // with one of its own. Per deployment (AGENTKEYSURL in compose); the default is compose's value.
    public string AgentKeysUrl { get; init; } = DefaultAgentKeysUrl;

    // Where the agent answers on the deployment's network: its relay knows which of a household's
    // own servers are connected now (ConnectedServers.Path), asked with the credential the agent
    // presents here, CALLERS__agent, which is the agent API's own secret. Per deployment (AGENTURL in
    // compose); the default is compose's value.
    public string AgentUrl { get; init; } = DefaultAgentUrl;

    // How long the operator's household list waits on the agent's listing before it shows the
    // servers as unknown: the relay asks each server for its tools, up to ten seconds.
    public TimeSpan AgentListingTimeout { get; init; } = TimeSpan.FromSeconds(15);

    // What the admin API holds an owner's changes to beyond the sections' own rules.
    public AdminSettings Admin { get; init; } = new();

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

    public const string DefaultAgentUrl = "http://agent:8080";

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

public record AdminSettings
{
    // The longest custom instructions an owner may give an agent, in characters: they go into every
    // turn's prompt, paid for on every call. The operator's set-household-config is not held to it.
    public int AgentInstructionsLength { get; init; } = 4000;

    // How many of an agent's earlier names and instructions are kept for an owner to restore.
    public int AgentVersionsKept { get; init; } = 5;
}

public record HouseholdConfigSettings
{
    // The deployment's master key (HOUSEHOLDCONFIG__MASTERKEY, the base64 of 32 random bytes): it
    // wraps each household's data key, which seals that household's secrets. Empty seals nothing — a
    // section with a secret cannot be saved and a sealed one cannot be read — which is what a stack
    // with no household secrets runs on. Losing it loses every household's secrets.
    public string MasterKey { get; init; } = "";
}

public record KeyProbeSettings
{
    // OpenRouter's API, whose key route answers the key's own metadata and bills nothing.
    public string OpenRouterUrl { get; init; } = "https://openrouter.ai/api/v1/";

    // Idealista's API, whose token exchange is what its search does first with the pair.
    public string IdealistaUrl { get; init; } = "https://api.idealista.com/";

    // Telegram's Bot API, whose getMe answers the bot a token belongs to.
    public string TelegramUrl { get; init; } = "https://api.telegram.org/";

    // A provider that has not answered by then could not be asked: the key is saved unchecked.
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(10);
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