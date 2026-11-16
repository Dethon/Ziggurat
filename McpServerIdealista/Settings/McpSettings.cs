using Domain.Security;
using Infrastructure.Clients.Accounts;
using Mcp.Hosting;

namespace McpServerIdealista.Settings;

public record McpSettings : IMcpHostSettings
{
    // This server's own credential, which its /mcp asks for (MCP__SHAREDSECRET).
    public McpGateSettings Mcp { get; init; } = new();

    public required IdealistaConfiguration Idealista { get; init; }

    // Where the accounts service answers the calling household's Idealista keys, which every search
    // is made with: the deployment holds none of its own. Asked with this server's own credential.
    public AccountsClientConfig Accounts { get; init; } = new();
}

public record IdealistaConfiguration
{
    public string ApiUrl { get; init; } = "https://api.idealista.com/";
}