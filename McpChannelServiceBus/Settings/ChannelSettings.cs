using Domain.Security;
using Mcp.Hosting;

namespace McpChannelServiceBus.Settings;

public record ChannelSettings : IMcpHostSettings
{
    // This server's own credential, which its /mcp asks for (MCP__SHAREDSECRET).
    public McpGateSettings Mcp { get; init; } = new();

    public required string ServiceBusConnectionString { get; init; }
    public required string PromptQueueName { get; init; }
    public required string ResponseQueueName { get; init; }
}