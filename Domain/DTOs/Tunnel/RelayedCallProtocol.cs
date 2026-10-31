namespace Domain.DTOs.Tunnel;

// How a shared deployment server reaches a household's own server: never at an address, which a
// household's server does not have, but through the relay's internal endpoint on the agent, which
// forwards the request down that server's tunnel. On the deployment's own network alone; the proxy
// routes nothing under /internal (DeploymentConformanceTests).
//
// The caller names itself and presents the credential it already holds for its own /mcp — the one
// the agent holds for it to dial it — so no secret exists for the relay's endpoint alone.
public static class RelayedCallProtocol
{
    public const string Path = "/internal/relay";

    // The shared server asking, by the name the agent dials it at (its own Mcp:Audience).
    public const string CallerHeader = "X-Ziggurat-Caller";

    // Where a request for the household server of this name goes: the rest of the path is the
    // server's own.
    public static string For(string serverName) => $"{Path}/{serverName}";

    // Whether a base address is the relay's endpoint rather than a server dialled directly.
    public static bool IsRelayed(Uri address) =>
        address.AbsolutePath.StartsWith(Path + "/", StringComparison.Ordinal);
}