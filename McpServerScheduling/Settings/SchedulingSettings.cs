using Domain.DTOs;

namespace McpServerScheduling.Settings;

public record SchedulingSettings
{
    public required string RedisConnectionString { get; init; }
    public int DispatchIntervalSeconds { get; init; } = 30;

    // Where a schedule with no deliverTo lands: the shared policy file's answer, the same one the
    // Home Assistant server gives a watch (Domain/delivery.json).
    public DeliverySettings Delivery { get; init; } = new();
}