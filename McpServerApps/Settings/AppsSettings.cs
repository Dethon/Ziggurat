using Domain.DTOs;
using Domain.Security;
using Infrastructure.Clients.Accounts;
using Mcp.Hosting;

namespace McpServerApps.Settings;

public record AppsSettings : IMcpHostSettings
{
    // This server's own credential, which its /mcp asks for (MCP__SHAREDSECRET).
    public McpGateSettings Mcp { get; init; } = new();

    // Where the accounts service answers the calling household's agent-names view: only the
    // household's own agents build apps.
    public AccountsClientConfig Accounts { get; init; } = new();

    // The volume: a directory per household, and in it one directory per built app holding its git
    // repository, its working tree and its metadata.
    public string StoragePath { get; init; } = "/data";

    // The registrable domain built apps are served under (`<slug>-<id>.<AppsDomain>`): per
    // deployment, so compose sets it (APPSDOMAIN); a test stack names a local one.
    public required string AppsDomain { get; init; }

    // Where the deployment's people reach the accounts service, the main host (PUBLICBASEURL, the
    // accounts service's own value): a navigation on an app origin with no app session is sent to the
    // app sign-in there (AppSignIn.Path). Empty sends nobody to sign in, and such a navigation is the 404.
    public string PublicBaseUrl { get; init; } = "";

    // The deployment's Redis (REDISCONNECTIONSTRING), where the accounts service says a person's
    // sessions ended (SessionsEnded), which ends their app sessions at once. Empty hears nothing: an app
    // session then stops only when its assertion comes due for renewal.
    public string RedisConnectionString { get; init; } = "";

    // Where the agent answers a built app's reads (AppReads.AgentPath), on the deployment's network
    // (AGENTURL). Empty reaches no agent: every read answers unavailable.
    public string AgentUrl { get; init; } = "";

    public AppSessionSettings Sessions { get; init; } = new();

    public AppQuotas Quotas { get; init; } = new();

    public AppCheckSettings Checks { get; init; } = new();

    public AppErrorSettings Errors { get; init; } = new();

    public AppListeners Listeners { get; init; } = new();

    public AppAskSettings Asks { get; init; } = new();

    // The deployment's retention policy, read from Domain's retention.json like every host's: an ask's images
    // wait in the upload store as long as an attachment is kept anywhere (ADR-0020).
    public RetentionSettings Retention { get; init; } = new();

    // Where a built app may load code, styles and fonts from besides itself, by exact version and
    // integrity hash (AppContentPolicy, OutsideCodePinning): origins, no paths.
    public IReadOnlyList<string> Cdns { get; init; } = [];
}

// The apps server's two listeners: /mcp for the deployment's network, app origins for the proxy.
// Neither answers the other's, by the socket a request arrived on (AppPagesListener).
public record AppListeners
{
    // The agent dials this one; nothing a browser sends reaches it through the proxy.
    public int Mcp { get; init; } = 8080;

    // The proxy sends every request for `*.<AppsDomain>` here and nowhere else of this server's.
    public int Pages { get; init; } = 8081;

    // The check browser's one way out (Checks/CheckEgress), when Checks:Browser:Proxy names it: no HTTP
    // here, bound only where that browser reaches it.
    public int Egress { get; init; } = 8082;
}

// What one household may spend of the deployment's shared storage. A refusal names the limit and
// its value, so the agent tells the person rather than retrying.
public record AppQuotas
{
    public int AppsPerHousehold { get; init; } = 20;

    // The whole repository: its working tree and its history.
    public long BytesPerRepository { get; init; } = 50L * 1024 * 1024;
}

// How long an app session lasts, and how long the assertion it holds is used before the session
// refresh is asked again.
public record AppSessionSettings
{
    // Its cookie's life too. Signing out, a revocation or a disabled member ends it sooner, by the
    // announcement or at its next renewal; running out costs a silent trip to the app sign-in.
    public TimeSpan Lifetime { get; init; } = TimeSpan.FromHours(12);

    // At most half an assertion's life (the accounts service's AssertionLifetime, ten minutes), so the
    // one an app session holds always has most of its life left for a hop to the agent; also the
    // longest a revoked session goes on opening an app when its announcement was missed.
    public TimeSpan AssertionKept { get; init; } = TimeSpan.FromMinutes(5);
}
// How a built app's asks (POST /_ask) are bounded: each is a turn of the building agent's that the page
// waits on whole. The instructions' bound is not here: it is the agent's prompt budget's
// (AppAsks.MaxInstructionsBytes), which no deployment may raise.
public record AppAskSettings
{
    // How long the page waits for the whole answer before the turn is stopped and the page told so.
    public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(2);

    // The longest text one ask may carry, in characters.
    public int MaxTextLength { get; init; } = 8_000;

    // The largest JSON schema one ask may carry, in UTF-8 bytes as the page wrote it. It rides the turn's
    // persisted message, uncounted in the history's budget (ADR 0044), so it stays a few kilobytes.
    public int MaxSchemaBytes { get; init; } = 8_192;

    // How long after its last turn a conversation an ask opened stays bound to that app and viewer.
    public TimeSpan FollowUpWindow { get; init; } = TimeSpan.FromHours(24);

    // How many asks of one app a household's people may send in any 24 hours, all of them together, unless
    // an owner sets otherwise (AskCaps): what bounds a page asking in a loop, since each ask is a model's turn
    // paid from the household's credit.
    public int DailyCap { get; init; } = 200;

    // How many images one ask may carry: more angles of one plate stop adding anything, and each is a picture
    // the model is sent again on every turn of the conversation within the hydration distance.
    public int MaxImages { get; init; } = 4;

    // The largest image, in bytes as the page sent it: the most any model an app may ask on takes in one
    // image (Anthropic's API refuses one over 5 MB). A page shrinks a photo before sending; nothing here does.
    public long MaxImageBytes { get; init; } = 5L * 1024 * 1024;

    // The image types an ask may carry, judged by their bytes (AppAskImages): JPEG, PNG, WebP and GIF are the
    // ones every image model takes. None here takes no images: the shipped file names them, as it names the
    // CDNs, since a default list would be added to, never replaced, by configuration naming fewer.
    public IReadOnlyList<string> ImageTypes { get; init; } = [];
}

// What the apps server keeps of the runtime errors its apps' pages report (`/_errors`).
public record AppErrorSettings
{
    // The newest this many per app are kept; an older one drops as a new one arrives.
    public int Kept { get; init; } = 100;

    // One broken loop is one session: past this many in its minute a report is answered 429 and dropped.
    // The client library sends at most ten a minute a page, so a person reloading stays under it.
    public int PerSessionPerMinute { get; init; } = 30;
}

// How far a check (`./check`, Checks/AppChecks) may go. Checks past a limit are refused, never queued: the
// agent hears the limit's name and value and tells the person rather than waiting on somebody else's.
public record AppCheckSettings
{
    // How long one check runs, every viewport included, before its browser is closed and its report says
    // so. At most the agent's call token refresh margin (callTokens:refreshMargin, 30 s): the page reads as
    // the check's person on the token of the call that ran it, which has at least that much life left when
    // it arrives (DeploymentConformanceTests).
    public TimeSpan Deadline { get; init; } = TimeSpan.FromSeconds(30);

    // How long a loaded page may take to go quiet on the network before it is shown as it is: a page
    // that polls never does.
    public TimeSpan Settle { get; init; } = TimeSpan.FromSeconds(5);

    // Each runs its own Chromium, so these bound the browser's memory as much as their fairness.
    public int PerHousehold { get; init; } = 1;
    public int Total { get; init; } = 2;

    public CheckBrowserSettings Browser { get; init; } = new();
}

// Where a check's Chromium runs. Either way it is launched for that check alone with the same options
// (Checks/CheckBrowser), so a browser on a server is the browser launched here, moved elsewhere.
public record CheckBrowserSettings
{
    // A Playwright browser server (`run-server --unsafe`, which launches a browser per connection with the
    // options the connection sends): in the deployment the `apps-browser` container, so the agent's code
    // never runs beside this server's credential, Redis and every household's repositories. Empty launches
    // the browser on this machine (a host run, the tests).
    public string Endpoint { get; init; } = "";

    // Where that browser reaches this server's pages listener, `host:port`. Empty is this machine's
    // loopback on Listeners.Pages, which is right for a browser on this machine and no other.
    public string Pages { get; init; } = "";

    // Where that browser reaches this server's egress proxy (Checks/CheckEgress, on Listeners.Egress),
    // `host:port`: in the deployment its only way out, since its network has none. Empty runs no proxy and
    // sends the browser through none (a host run, most tests).
    public string Proxy { get; init; } = "";
}