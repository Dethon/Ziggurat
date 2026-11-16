using Domain.Outposts;

namespace WebChat.Client.Components.Admin;

// Where an owner puts a tunnel credential they just minted, by the server's name: the tenant kit reads
// one variable per server it runs (TenantKit/.env, held to this list by test), a machine running the
// outpost reads CREDENTIAL, and any other server is given it as its own tunnel credential setting.
public static class ServerCredentialPlace
{
    public static readonly IReadOnlyList<string> KitServers =
        ["websearch", "vault", "library", "sandbox", "printer", "homeassistant", "voice", "lemonade"];

    // What each of the kit's servers gives the household's agents, for an owner choosing which to run.
    private static readonly IReadOnlyDictionary<string, string> _gives = new Dictionary<string, string>
    {
        ["websearch"] = "searching the web, reading pages and signing in to sites",
        ["vault"] = "the household's notes (an Obsidian vault)",
        ["library"] = "downloads and the media library",
        ["sandbox"] = "running code and commands",
        ["printer"] = "printing",
        ["homeassistant"] = "the home's devices, through Home Assistant",
        ["voice"] = "voice satellites, and timers that ring on them",
        ["lemonade"] = "the models of a Lemonade box on the household's own network, in its model menu " +
                       "(saved \"through the tenant kit\" on the Keys page, and run with --profile lemonade)"
    };

    public static string? WhatItGives(string serverName) => _gives.GetValueOrDefault(serverName);

    public static string For(string serverName) =>
        serverName.StartsWith(OutpostServerName.Prefix, StringComparison.Ordinal)
            ? "the machine's CREDENTIAL when it runs the outpost"
            : KitServers.Contains(serverName)
                ? $"the tenant kit's TUNNEL_CREDENTIAL_{serverName.ToUpperInvariant()} in its .env"
                : "its own tunnel credential (MCP__TUNNEL__CREDENTIAL)";
}