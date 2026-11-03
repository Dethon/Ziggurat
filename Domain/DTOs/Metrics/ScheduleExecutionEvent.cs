using Domain.Channels;
using Domain.DTOs.Channel;

namespace Domain.DTOs.Metrics;

public record ScheduleExecutionEvent : MetricEvent
{
    public required string ScheduleId { get; init; }
    public required string Prompt { get; init; }
    public required long DurationMs { get; init; }
    public required bool Success { get; init; }
    public string? Error { get; init; }

    public static ScheduleExecutionEvent? FromMessage(
        ChannelMessage message, long durationMs, bool success, string? error)
    {
        if (message.Origin is not { Kind: MessageOriginKind.Schedule, ScheduleId: { } scheduleId })
        {
            return null;
        }

        return new ScheduleExecutionEvent
        {
            ScheduleId = scheduleId,
            AgentId = message.AgentId ?? "default",
            Prompt = FireReports.InstructionOf(message.Content),
            DurationMs = durationMs,
            Success = success,
            Error = error
        };
    }
}