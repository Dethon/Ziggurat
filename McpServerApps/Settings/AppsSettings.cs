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

    public AppSessionSettings Sessions { get; init; } = new();

    public AppQuotas Quotas { get; init; } = new();

    public AppListeners Listeners { get; init; } = new();

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