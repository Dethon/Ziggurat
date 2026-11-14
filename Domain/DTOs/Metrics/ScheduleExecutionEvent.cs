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

    // A household's own fire is recorded without what it asked (withPrompt false): the operator's
    // dashboard reads every household's, and a schedule's prompt is as private as the conversation
    // that wrote it. Only the home's are the operator's own.
    public static ScheduleExecutionEvent? FromMessage(
        ChannelMessage message, long durationMs, bool success, string? error, bool withPrompt = true)
    {
        if (message.Origin is not { Kind: MessageOriginKind.Schedule, ScheduleId: { } scheduleId })
        {
            return null;
        }

        return new ScheduleExecutionEvent
        {
            ScheduleId = scheduleId,
            AgentId = message.AgentId ?? "default",
            Prompt = withPrompt ? FireReports.InstructionOf(message.Content) : "",
            DurationMs = durationMs,
            Success = success,
            Error = error
        };
    }
}