using Domain.DTOs;
using Domain.DTOs.Tunnel;

namespace Domain.Agents;

// What a household keeps from its agents and their workers, as one list of servers: the shared
// servers its features turned off, by the address the deployment dials them at, and its own servers
// it never set up, by their server name. An endpoint is matched as the kind of server it is — a URL
// as a server address (SharedServers.Names), a name by the name — and never across, so a household
// server named like a shared server's host is not withheld by that server's address, nor a shared
// server by a name.
public static class WithheldServers
{
    public static bool Withholds(IEnumerable<string> withheld, string endpoint) =>
        TunnelCredentialProtocol.IsServerName(endpoint)
            ? withheld.Contains(endpoint, StringComparer.Ordinal)
            : SharedServers.Names(withheld, endpoint);
}