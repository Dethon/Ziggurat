using Domain.DTOs.Household;
using Domain.DTOs.Tunnel;
using Domain.Security;
using JetBrains.Annotations;

namespace Domain.DTOs;

// The servers the deployment runs for every household, where the agent dials each, by the household
// feature that offers it (`sharedServers` in the agent's settings). A household's features decide
// which of them its agents and their workers reach: a feature turned off withholds its server from
// every definition that names it, a template's, a seeded one's, a worker profile's or a person's own.
// A server the deployment does not run is left unset, and nothing is withheld on its account.
[PublicAPI]
public sealed record SharedServers
{
    public string? Scheduling { get; init; }
    public string? Timers { get; init; }
    public string? Sugary { get; init; }
    public string? Idealista { get; init; }
    public string? Apps { get; init; }

    // The name an agent's endpoint is configured under, which a built app's manifest declares the servers
    // its asks may use by: a shared server its key here, a household's own server its own name. Never a
    // host and port, which are the deployment's topology and would break a published app when they moved.
    // An own server named as a shared one has none, so a name picks one server; null for any other address.
    public string? NameOf(string endpoint) =>
        TunnelCredentialProtocol.IsServerName(endpoint)
            ? (Listed().Any(s => s.Name == endpoint) ? null : endpoint)
            : Listed().FirstOrDefault(s => s.Address is not null && Names([s.Address], endpoint)).Name;

    public IReadOnlyList<string> All => [.. Listed().Select(s => s.Address).OfType<string>()];

    // The servers this household's features keep from its agents, and the timers too where nothing of
    // the household's can ring one.
    public IReadOnlyList<string> WithheldBy(FeaturesSection features, bool ringsNowhere = false) =>
    [
        .. Listed()
            .Where(s => s.Address is not null && (!s.Offered(features) || ringsNowhere && s.Address == Timers))
            .Select(s => s.Address!)
    ];

    // Whether an endpoint is one of these servers: compared as servers (ServerAddress: scheme, host,
    // port and path), not as strings, so a host spelt in capitals is the same server.
    public static bool Names(IEnumerable<string> servers, string endpoint) =>
        ServerAddress.FromUrl(endpoint) is { } server
        && servers.Any(address => ServerAddress.FromUrl(address) is { } other && other == server);

    // Each by its key in the settings, the name it is configured and declared under.
    private IEnumerable<(string Name, string? Address, Func<FeaturesSection, bool> Offered)> Listed() =>
    [
        ("scheduling", Scheduling, f => f.Scheduling),
        ("timers", Timers, f => f.Timers),
        ("sugary", Sugary, f => f.Sugary),
        ("idealista", Idealista, f => f.Idealista),
        ("apps", Apps, f => f.Apps)
    ];
}