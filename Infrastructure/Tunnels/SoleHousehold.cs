namespace Infrastructure.Tunnels;

// Where a request that names no household goes: a browse page's link, a timer ringing. In this phase
// the only household with servers is the home one, so such a request is for the one household whose
// server of that name has its tunnel open — and with none open, or more than one, it is answered as
// unreachable with the reason rather than sent to whichever came first.
//
// It is a stopgap with its edge in plain sight: once a second household runs servers (4b), the
// request must name its household. The web search server already knows it where a link is minted —
// web_login's call token carries the person's account — and the timers server where a timer is.
public abstract record HouseholdServer
{
    public sealed record Open(OpenTunnel Tunnel) : HouseholdServer;

    public sealed record Unreachable(string Reason) : HouseholdServer;
}

public static class SoleHousehold
{
    public static HouseholdServer Find(TunnelRegistry registry, string serverName) =>
        registry.Open.Where(t => t.ServerName == serverName && t.IsOpen).ToList() switch
        {
            [var only] => new HouseholdServer.Open(only),
            [] => new HouseholdServer.Unreachable($"{serverName} is offline: its tunnel is not open"),
            _ => new HouseholdServer.Unreachable(
                $"more than one household's {serverName} is connected, and this request names no household")
        };
}