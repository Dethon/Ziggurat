namespace Infrastructure.Agents;

// Where a channel endpoint that names a household's server is reached: every opening of the home
// household's server of that name, from now on, each with its endpoint and a token cancelled when
// that opening goes. A channel connection lives longer than any session, so it cannot ask once, as a
// turn does (IOwnServers): it follows the server from one opening to the next, and each opening is a
// connection generation of its own (ADR 0012). The deployment's answer is the relay's tunnels
// (RelayedOwnServers).
//
// The home household alone in this phase: a channel connection is one per channel id, and the only
// household with servers is the operator's.
public interface IOwnChannelServers
{
    IAsyncEnumerable<OwnServerOpening> WatchAsync(string serverName, CancellationToken ct);
}

// The opening's household is the tunnel credential's, verified by the accounts service as it opened;
// Home says that household is the operator's.
public sealed record OwnServerOpening(
    McpServerEndpoint Endpoint, CancellationToken Closed, string HouseholdId, bool Home);