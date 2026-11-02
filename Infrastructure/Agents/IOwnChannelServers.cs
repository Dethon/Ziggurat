using Domain.DTOs.Channel;

namespace Infrastructure.Agents;

// Where a channel endpoint that names a household's server is reached: every opening of every
// household's server of that name, from now on, each with its endpoint, its household and a token
// cancelled when that opening goes. A channel connection lives longer than any session, so it cannot
// ask once, as a turn does (IOwnServers): it follows the server from one opening to the next, and each
// opening is a connection generation of its own (ADR 0012). Each household's tunnel is a connection of
// that household's (ChannelConnectionHost). The deployment's answer is the relay's tunnels
// (RelayedOwnServers).
public interface IOwnChannelServers
{
    IAsyncEnumerable<OwnServerOpening> WatchAsync(string serverName, CancellationToken ct);
}

// The opening's household is the tunnel credential's, verified by the accounts service as it opened,
// marked Home when that household is the operator's.
public sealed record OwnServerOpening(McpServerEndpoint Endpoint, CancellationToken Closed, ChannelHousehold Household)
{
    public string HouseholdId => Household.HouseholdId!;
}