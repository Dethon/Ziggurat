using Domain.DTOs;
using Domain.Security;
using Infrastructure.Clients.Accounts;
using Mcp.Hosting;

namespace McpServerScheduling.Settings;

public record SchedulingSettings : IMcpHostSettings
{
    // This server's own credential, which its /mcp asks for (MCP__SHAREDSECRET).
    public McpGateSettings Mcp { get; init; } = new();

    public required string RedisConnectionString { get; init; }
    public int DispatchIntervalSeconds { get; init; } = 30;

    // Where a schedule with no deliverTo lands: the shared policy file's answer, the same one the
    // Home Assistant server gives a watch (Domain/delivery.json).
    public DeliverySettings Delivery { get; init; } = new();

    // Where the accounts service answers a household's settings: the zone its schedules are read and
    // fired in (.claude/rules/household-config.md). Compose's default address; asked with this
    // server's own credential.
    public AccountsClientConfig Accounts { get; init; } = new();
}