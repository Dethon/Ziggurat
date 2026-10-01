using Domain.Security;
using Mcp.Hosting;

namespace McpChannelServiceBus.Settings;

public record ChannelSettings : IMcpHostSettings
{
    // The deployment secret this server's /mcp asks for (MCP__SHAREDSECRET).
    public McpGateSettings Mcp { get; init; } = new();

    public required string ServiceBusConnectionString { get; init; }
    public required string PromptQueueName { get; init; }
    public required string ResponseQueueName { get; init; }
}