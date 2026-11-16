using Domain.DTOs.Tunnel;
using Domain.Outposts;
using Domain.Security;
using Mcp.Hosting;

namespace McpServerOutpost.Settings;

public record OutpostSettings : IMcpHostSettings
{
    // The name the mount is addressed by, so a person can tell their laptop from their desktop
    // when they talk to the agent. The credential is minted for `outpost-<Name>`, so it is a lowercase
    // DNS label's tail (OutpostServerName.IsValidName).
    public required string Name { get; init; }

    // Where files land and commands run. It is the mount's declared workspace, and — when the
    // outpost is jailed — the only part of the machine any operation will touch.
    public required string WorkingDirectory { get; init; }

    public bool Jailed { get; init; }

    // Off unless asked for: exposing somebody's files must not imply exposing a shell on their
    // computer, and the safe thing has to be what happens when the binary is run without reading
    // anything.
    public bool Exec { get; init; }

    // The command runner's bounds. Not flags: nobody starting a binary on their laptop has an
    // opinion about an output cap, and these are the sandbox's own numbers so a command behaves
    // the same wherever the agent runs it. They bind from the environment like anything else if a
    // deployment ever needs to differ.
    public int DefaultTimeoutSeconds { get; init; } = 60;
    public int MaxTimeoutSeconds { get; init; } = 1800;
    public int OutputCapBytes { get; init; } = 65536;

    // Comma-separated, replacing the shared list wholesale for this machine — a computer full of
    // unusual source files is still readable. Absent leaves the shared list alone.
    public string? Ext { get; init; }

    // The deployment this machine joins: its main address (`https://assistants.<domain>`), whose
    // tunnel route the machine opens its tunnel on, or that route itself (`wss://…/tunnel`).
    // Required: the tunnel is the only way the agent reaches the machine, which listens on loopback.
    public required string Hub { get; init; }

    // The tunnel credential the operator minted for `outpost-<Name>`. The one value that is a secret,
    // so the one that arrives as an environment variable (CREDENTIAL) rather than as a flag: a
    // command line is visible to every process on the machine.
    public string Credential { get; init; } = "";

    // How the tunnel is kept alive and brought back: Domain's shared tunnel.json, the same numbers
    // every tunnelled server and the relay keep (read by OutpostFlags), never a flag.
    public TunnelSettings TunnelPolicy { get; init; } = new();

    // The tunnel this machine opens: the hub's tunnel route, its credential, and the server name the
    // credential was minted for. Tunnelled, the host gates /mcp behind the credential the relay
    // presents and checks every call token against the keys the relay hands over the tunnel, as it
    // does for every household server. Computed rather than bound, so no variable of the
    // deployment's names (MCP__SHAREDSECRET, MCP__TUNNEL__*) can reach it.
    McpGateSettings IMcpHostSettings.Mcp => new()
    {
        Tunnel = TunnelPolicy with
        {
            Hub = TunnelHub(Hub),
            Credential = Credential,
            ServerName = OutpostServerName.For(Name)
        }
    };

    // The hub a person types is the deployment's address, which is what they know it by; the tunnel
    // is its route there, over the WebSocket scheme of the same security. A WebSocket URL is the
    // route already.
    public static string TunnelHub(string hub)
    {
        if (!Uri.TryCreate(hub, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
        {
            return hub;
        }

        return new UriBuilder(uri)
        {
            Scheme = uri.Scheme == Uri.UriSchemeHttps ? "wss" : "ws",
            Port = uri.IsDefaultPort ? -1 : uri.Port,
            Path = uri.AbsolutePath.TrimEnd('/') + TunnelProtocol.Path
        }.Uri.ToString();
    }
}