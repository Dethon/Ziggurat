using Domain.DTOs.Household;
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

    public IReadOnlyList<string> All => [.. Listed().Select(s => s.Address).OfType<string>()];

    // The servers this household's features keep from its agents.
    public IReadOnlyList<string> WithheldBy(FeaturesSection features) =>
    [
        .. Listed()
            .Where(s => s.Address is not null && !s.Offered(features))
            .Select(s => s.Address!)
    ];

    // Whether an endpoint is one of these servers: compared as servers (ServerAddress: scheme, host,
    // port and path), not as strings, so a host spelt in capitals is the same server.
    public static bool Names(IEnumerable<string> servers, string endpoint) =>
        ServerAddress.FromUrl(endpoint) is { } server
        && servers.Any(address => ServerAddress.FromUrl(address) is { } other && other == server);

    private IEnumerable<(string? Address, Func<FeaturesSection, bool> Offered)> Listed() =>
    [
        (Scheduling, f => f.Scheduling),
        (Timers, f => f.Timers),
        (Sugary, f => f.Sugary),
        (Idealista, f => f.Idealista)
    ];
}