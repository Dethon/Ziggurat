using Domain.DTOs;
using Domain.Security;
using Mcp.Hosting;

namespace McpServerScheduling.Settings;

public record SchedulingSettings : IMcpHostSettings
{
    // The deployment secret this server's /mcp asks for (MCP__SHAREDSECRET).
    public McpGateSettings Mcp { get; init; } = new();

    public required string RedisConnectionString { get; init; }
    public int DispatchIntervalSeconds { get; init; } = 30;

    // Where a schedule with no deliverTo lands: the shared policy file's answer, the same one the
    // Home Assistant server gives a watch (Domain/delivery.json).
    public DeliverySettings Delivery { get; init; } = new();
}