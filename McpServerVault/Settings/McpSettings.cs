using Domain.Security;
using Infrastructure.Metrics;
using Mcp.Hosting;

namespace McpServerVault.Settings;

public record McpSettings : IMcpHostSettings
{
    // Its heartbeat, and its helpers', posted on its tunnel's metrics back-call (METRICS__URL): the
    // deployment's dashboard has no other way to see it. Unset outside a kit, which posts nothing.
    public MetricsFeedConfig Metrics { get; init; } = new();

    // This server's own credential, which its /mcp asks for (MCP__SHAREDSECRET).
    public McpGateSettings Mcp { get; init; } = new();

    public required string VaultPath { get; init; }
    public required string[] AllowedExtensions { get; init; }
}