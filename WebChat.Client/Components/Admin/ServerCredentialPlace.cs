using Domain.Outposts;

namespace WebChat.Client.Components.Admin;

// Where an owner puts a tunnel credential they just minted, by the server's name: the tenant kit reads
// one variable per server (TenantKit/.env), and a machine running the outpost reads CREDENTIAL.
public static class ServerCredentialPlace
{
    public static string For(string serverName) =>
        serverName.StartsWith(OutpostServerName.Prefix, StringComparison.Ordinal)
            ? "the machine's CREDENTIAL when it runs the outpost"
            : $"the tenant kit's TUNNEL_CREDENTIAL_{serverName.Replace('-', '_').ToUpperInvariant()} in its .env";
}