namespace Domain.DTOs.Tunnel;

// The names a household's servers go by when the deployment itself must find one — a credential is
// minted for the name, the tunnel claims it, and the relay looks it up or lets only it make a
// back-call — rather than an agent
// definition naming it. The tenant kit runs its servers under these.
public static class HouseholdServerNames
{
    // Its pages listener answers the browse pages a person opens from a link.
    public const string WebSearch = "websearch";

    // Its announce, dismiss and satellite API is what the timers server rings and validates through.
    public const string Voice = "voice";

    // The one server whose exec bridge back-call is its own: its FUSE daemon reads the session's mounts.
    public const string Sandbox = "sandbox";

    // A household's own Lemonade box, through its forwarder: the agent asks it for its models and runs
    // that household's Lemonade turns on it. Never a tool server.
    public const string Lemonade = "lemonade";

    // Whether a household's server of that name is a tool server: every one but the Lemonade
    // forwarder, which serves no MCP. No agent dials it, and nothing asks it for tools.
    public static bool ServesTools(string serverName) => serverName != Lemonade;
}