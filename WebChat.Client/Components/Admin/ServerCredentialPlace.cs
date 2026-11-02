using Domain.Outposts;

namespace WebChat.Client.Components.Admin;

// Where an owner puts a tunnel credential they just minted, by the server's name: the tenant kit reads
// one variable per server it runs (TenantKit/.env, held to this list by test), a machine running the
// outpost reads CREDENTIAL, and any other server is given it as its own tunnel credential setting.
public static class ServerCredentialPlace
{
    public static readonly IReadOnlyList<string> KitServers =
        ["websearch", "vault", "library", "sandbox", "printer", "homeassistant", "voice"];

    public static string For(string serverName) =>
        serverName.StartsWith(OutpostServerName.Prefix, StringComparison.Ordinal)
            ? "the machine's CREDENTIAL when it runs the outpost"
            : KitServers.Contains(serverName)
                ? $"the tenant kit's TUNNEL_CREDENTIAL_{serverName.ToUpperInvariant()} in its .env"
                : "its own tunnel credential (MCP__TUNNEL__CREDENTIAL)";
}