using Domain.DTOs.Tunnel;

namespace Infrastructure.Tunnels;

// Where the relay's back-calls go in this deployment. The agent's own routes are asked where the
// agent listens, so only the services beside it are named; the accounts service's address is the one
// the relay already verifies credentials at.
public sealed record BackCallSettings
{
    // The shared Lemonade voice's speech goes to (BackCallProtocol.Lemonade), on the compose network.
    public required string LemonadeUrl { get; init; }
}

// The relay's allowlist: for each back-call, the deployment service it reaches and the only paths it
// may ask there, every one a POST. Nothing else a household's server sends is let through — not
// another accounts route (a tunnel credential's verify, the Telegram bot's, the chat hub's sessions),
// not Lemonade's model management, not any agent route but the bridge's and topic minting.
public sealed class BackCallTargets(Uri accounts, Uri lemonade)
{
    private const string AccountsInternal = "/accounts/internal";

    private static readonly Dictionary<string, Allowed> _allowed = new(StringComparer.Ordinal)
    {
        [BackCallProtocol.Accounts] = new(Target.Accounts, [AccountsInternal + "/resolve", AccountsInternal + "/guest"], []),
        [BackCallProtocol.Lemonade] = new(Target.Lemonade, ["/v1/audio/transcriptions", "/v1/audio/speech"], []),
        [BackCallProtocol.VfsBridge] = new(Target.Agent, [], [BridgeRoute + "/"]),
        [BackCallProtocol.Topics] = new(Target.Agent, [Domain.DTOs.Channel.TopicMinting.Path], []),
        [BackCallProtocol.Metrics] = new(Target.Relay, [BackCallProtocol.MetricsPath], [])
    };

    // Agent/App/VfsBridgeApi.Route, which Infrastructure cannot see; BackCallGatewayTests asks the
    // real bridge at this path through the tunnel, so the two cannot drift apart.
    public const string BridgeRoute = "/api/vfs-bridge";

    public static BackCallTargets From(BackCallSettings settings, string accountsUrl) =>
        new(new Uri(accountsUrl.TrimEnd('/') + "/"), new Uri(settings.LemonadeUrl.TrimEnd('/') + "/"));

    public bool Knows(string tag) => _allowed.ContainsKey(tag);

    public bool Allows(string tag, string path) =>
        _allowed.TryGetValue(tag, out var allowed)
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

    private enum Target
    {
        Accounts,
        Lemonade,
        Agent,
        Relay
    }

    private sealed record Allowed(Target Target, string[] Paths, string[] Prefixes);
}