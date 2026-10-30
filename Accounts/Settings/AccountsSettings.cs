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

    // How long an invite or recovery link stays redeemable.
    public int LinkLifetimeDays { get; init; } = 7;

    public Uri PublicBase => new(PublicBaseUrl.TrimEnd('/') + "/");

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