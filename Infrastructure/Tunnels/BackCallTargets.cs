using Domain.DTOs.Household;
using Domain.DTOs.Metrics;
using Domain.DTOs.Tunnel;
using Domain.Outposts;

namespace Infrastructure.Tunnels;

// Where the relay's back-calls go in this deployment. The agent's own routes are asked where the
// agent listens, so only the services beside it are named; the accounts service's address is the one
// the relay already verifies credentials at.
public sealed record BackCallSettings
{
    // The shared Lemonade voice's speech goes to (BackCallProtocol.Lemonade), on the compose network.
    public required string LemonadeUrl { get; init; }
}

// The relay's allowlist: for each back-call, the servers that may make it, the deployment service it
// reaches and the only paths it may ask there, every one a POST. Nothing else a household's server
// sends is let through — not another accounts route (a tunnel credential's verify, the Telegram bot's,
// the chat hub's sessions), not Lemonade's model management, not any agent route but the bridge's and
// topic minting — and no back-call from a server it is not for: the accounts back-call mints voice
// assertions for anybody in the household, so a printer's or an outpost's credential must not reach it,
// and web search reaches its one key there and none of voice's routes.
public sealed class BackCallTargets(Uri accounts, Uri lemonade)
{
    private const string AccountsInternal = "/accounts/internal";

    private static readonly Dictionary<string, Allowed> _allowed = new(StringComparer.Ordinal)
    {
        [BackCallProtocol.Accounts] = new(Target.Accounts,
            new Grant(IsVoice, [AccountsInternal + "/resolve", AccountsInternal + "/guest"], []),
            new Grant(
                server => server == HouseholdServerNames.WebSearch,
                [HouseholdConfigProtocol.PathOf(HouseholdConfigProtocol.BraveSearchView)], [])),
        [BackCallProtocol.Lemonade] = new(Target.Lemonade,
            new Grant(IsVoice, ["/v1/audio/transcriptions", "/v1/audio/speech"], [])),
        [BackCallProtocol.VfsBridge] = new(Target.Agent,
            new Grant(server => server == HouseholdServerNames.Sandbox, [], [BridgeRoute + "/"])),
        [BackCallProtocol.Topics] = new(Target.Agent,
            new Grant(IsVoice, [Domain.DTOs.Channel.TopicMinting.Path], [])),
        // Every household's server may report on itself; an outpost publishes nothing.
        [BackCallProtocol.Metrics] = new(Target.Relay,
            new Grant(server => OutpostServerName.NameOf(server) is null, [BackCallProtocol.MetricsPath], []))
    };

    // Agent/App/VfsBridgeApi.Route, which Infrastructure cannot see; BackCallGatewayTests asks the
    // real bridge at this path through the tunnel, so the two cannot drift apart.
    public const string BridgeRoute = "/api/vfs-bridge";

    public static BackCallTargets From(BackCallSettings settings, string accountsUrl) =>
        new(new Uri(accountsUrl.TrimEnd('/') + "/"), new Uri(settings.LemonadeUrl.TrimEnd('/') + "/"));

    // The heartbeat the sandbox's server beats (McpServerSandbox's AddMetricsPublishing).
    public const string SandboxHeartbeat = "mcp-sandbox";

    public bool Knows(string tag) => _allowed.ContainsKey(tag);

    // The model's commands run in the sandbox's container and can dial its metrics back-call, so
    // the sandbox publishes its own heartbeat and nothing else: a command can keep that one tile
    // green, never post a household's costs, errors or another server's heartbeat (user, 2026-11-12).
    public static bool MayPublish(string serverName, MetricEvent published) =>
        serverName != HouseholdServerNames.Sandbox
        || published is HeartbeatEvent { Service: SandboxHeartbeat };

    public bool Allows(string tag, string serverName, string path) =>
        _allowed.TryGetValue(tag, out var allowed)
        && allowed.Grants.Any(grant => grant.From(serverName)
            && (grant.Paths.Contains(path, StringComparer.Ordinal)
                || grant.Prefixes.Any(prefix => path.StartsWith(prefix, StringComparison.Ordinal) && !path.Contains(".."))));

    // The accounts service is shown the tunnel's own credential, so it reads who asks off it.
    public bool PresentsCredential(string tag) => tag == BackCallProtocol.Accounts;

    public Uri BaseOf(string tag, Func<Uri> agent) => _allowed[tag].Target switch
    {
        Target.Accounts => accounts,
        Target.Lemonade => lemonade,
        Target.Agent => agent(),
        _ => throw new InvalidOperationException($"The '{tag}' back-call is answered by the relay itself.")
    };

    private static bool IsVoice(string serverName) => serverName == HouseholdServerNames.Voice;

    private enum Target
    {
        Accounts,
        Lemonade,
        Agent,
        Relay
    }

    private sealed record Allowed(Target Target, params Grant[] Grants);

    // Which servers may ask which paths of a back-call's target: one target can serve two servers
    // different routes, and neither reaches the other's.
    private sealed record Grant(Func<string, bool> From, string[] Paths, string[] Prefixes);
}