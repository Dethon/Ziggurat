using Domain.DTOs.Tunnel;

namespace Domain.Outposts;

// How an outpost's tunnel is told apart from a household's own server: by the server name its
// credential was minted for, `outpost-<NAME>`. The name is the verified credential's, never anything
// the machine says on the open — the relay refuses a tunnel claiming a name its credential was not
// minted for — so a machine is an outpost because the operator minted it as one, and one tunnel
// registry keyed by (household, server name) holds both kinds without an outpost ever replacing a
// household's server of the same NAME. One spelling for both ends: the outpost claiming its name and
// the agent reading which of a household's open tunnels are machines.
public static class OutpostServerName
{
    public const string Prefix = "outpost-";

    public static string For(string name) => Prefix + name;

    // The outpost's own NAME — what its mount is addressed by — or null for a tunnel that is not one.
    public static string? NameOf(string serverName) =>
        serverName.StartsWith(Prefix, StringComparison.Ordinal) && serverName.Length > Prefix.Length
            ? serverName[Prefix.Length..]
            : null;

    // Whether a machine may be started under this NAME: its server name must be one a credential can
    // be minted for (a lowercase DNS label).
    public static bool IsValidName(string name) => TunnelCredentialProtocol.IsServerName(For(name));
}