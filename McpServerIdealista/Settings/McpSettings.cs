using Domain.Security;
using Mcp.Hosting;

namespace McpServerIdealista.Settings;

public record McpSettings : IMcpHostSettings
{
    // This server's own credential, which its /mcp asks for (MCP__SHAREDSECRET).
    public McpGateSettings Mcp { get; init; } = new();

    public required IdealistaConfiguration Idealista { get; init; }
}

public record IdealistaConfiguration
{
    public required string ApiKey { get; init; }
    public required string ApiSecret { get; init; }
    public string ApiUrl { get; init; } = "https://api.idealista.com/";
}