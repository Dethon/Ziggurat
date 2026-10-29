using Domain.Security;
using Mcp.Hosting;

namespace McpServerVault.Settings;

public record McpSettings : IMcpHostSettings
{
    // This server's own credential, which its /mcp asks for (MCP__SHAREDSECRET).
    public McpGateSettings Mcp { get; init; } = new();

    public required string VaultPath { get; init; }
    public required string[] AllowedExtensions { get; init; }
}