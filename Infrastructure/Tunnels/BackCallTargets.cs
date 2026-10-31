using Domain.DTOs.Tunnel;
using Domain.Outposts;

namespace Infrastructure.Tunnels;

// Where the relay's back-calls go in this deployment. The agent's own routes are asked where the
// agent listens, so only the services beside it are named; the accounts service's address is the one
// the relay already verifies credentials at.
public sealed record BackCallSettings
{
    // The shared Lemonade voice's speech goes to (BackCallProtocol.Lemonade), on the compose network.
    public string LemonadeUrl { get; init; } = "http://lemonade:13305";
}

// The relay's allowlist: for each back-call, the servers that may make it, the deployment service it
// reaches and the only paths it may ask there, every one a POST. Nothing else a household's server
// sends is let through — not another accounts route (a tunnel credential's verify, the Telegram bot's,
// the chat hub's sessions), not Lemonade's model management, not any agent route but the bridge's and
// topic minting — and no back-call from a server it is not for: the accounts back-call mints voice
// assertions for anybody in the household, so a printer's or an outpost's credential must not reach it.
public sealed class BackCallTargets(Uri accounts, Uri lemonade)
{
    private const string AccountsInternal = "/accounts/internal";

    private static readonly Dictionary<string, Allowed> _allowed = new(StringComparer.Ordinal)
    {
        [BackCallProtocol.Accounts] = new(
            IsVoice, Target.Accounts, [AccountsInternal + "/resolve", AccountsInternal + "/guest"], []),
        [BackCallProtocol.Lemonade] = new(IsVoice, Target.Lemonade, ["/v1/audio/transcriptions", "/v1/audio/speech"], []),
        [BackCallProtocol.VfsBridge] = new(
            server => server == HouseholdServerNames.Sandbox, Target.Agent, [], [BridgeRoute + "/"]),
        [BackCallProtocol.Topics] = new(IsVoice, Target.Agent, [Domain.DTOs.Channel.TopicMinting.Path], []),
        // Every household's server may report on itself; an outpost publishes nothing.
        [BackCallProtocol.Metrics] = new(
            server => OutpostServerName.NameOf(server) is null, Target.Relay, [BackCallProtocol.MetricsPath], [])
    };

    // Agent/App/VfsBridgeApi.Route, which Infrastructure cannot see; BackCallGatewayTests asks the
    // real bridge at this path through the tunnel, so the two cannot drift apart.
    public const string BridgeRoute = "/api/vfs-bridge";

    public static BackCallTargets From(BackCallSettings settings, string accountsUrl) =>
        new(new Uri(accountsUrl.TrimEnd('/') + "/"), new Uri(settings.LemonadeUrl.TrimEnd('/') + "/"));

    public bool Knows(string tag) => _allowed.ContainsKey(tag);

    public bool Allows(string tag, string serverName, string path) =>
        _allowed.TryGetValue(tag, out var allowed)
        && allowed.From(serverName)
        && (allowed.Paths.Contains(path, StringComparer.Ordinal)
            || allowed.Prefixes.Any(prefix => path.StartsWith(prefix, StringComparison.Ordinal) && !path.Contains("..")));

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

    private sealed record Allowed(Func<string, bool> From, Target Target, string[] Paths, string[] Prefixes);
}