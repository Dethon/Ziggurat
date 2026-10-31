namespace Infrastructure.Tunnels;

// Where a request that names no household goes: a browse page's link, a timer's ring. In this phase
// the only household with servers is the home one, so such a request is for the home household's
// server of that name — the opening the accounts service called home when it verified its credential
// (OpenTunnel.Home), as a channel endpoint named by server name is. With none open it is answered as
// unreachable with the reason; another household's server of the same name is never a fallback.
//
// It has its edge in plain sight: once a second household runs servers (4b), the request must name
// its household. The web search server already knows it where a link is minted — web_login's call
// token carries the person's account — and the timers server where a timer is created.
public abstract record HouseholdServer
{
    public sealed record Open(OpenTunnel Tunnel) : HouseholdServer;

    public sealed record Unreachable(string Reason) : HouseholdServer;
}

public static class HomeHousehold
{
    public static HouseholdServer Find(TunnelRegistry registry, string serverName) =>
        registry.Open.FirstOrDefault(t => t is { Home: true, IsOpen: true } && t.ServerName == serverName) is { } tunnel
            ? new HouseholdServer.Open(tunnel)
            : new HouseholdServer.Unreachable($"{serverName} is offline: its tunnel is not open");
}