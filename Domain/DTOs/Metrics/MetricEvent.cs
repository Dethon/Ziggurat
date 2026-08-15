using System.Text.Json.Serialization;

namespace Domain.DTOs.Metrics;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(TokenUsageEvent), "token_usage")]
[JsonDerivedType(typeof(ToolCallEvent), "tool_call")]
[JsonDerivedType(typeof(ErrorEvent), "error")]
[JsonDerivedType(typeof(ScheduleExecutionEvent), "schedule_execution")]
[JsonDerivedType(typeof(HeartbeatEvent), "heartbeat")]
[JsonDerivedType(typeof(MemoryRecallEvent), "memory_recall")]
[JsonDerivedType(typeof(MemoryExtractionEvent), "memory_extraction")]
[JsonDerivedType(typeof(MemoryDreamingEvent), "memory_dreaming")]
[JsonDerivedType(typeof(ContextTruncationEvent), "context_truncation")]
[JsonDerivedType(typeof(LatencyEvent), "latency")]
[JsonDerivedType(typeof(VoiceEvent), "voice")]
[JsonDerivedType(typeof(OutpostEvent), "outpost")]
public abstract record MetricEvent
{
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
    public string? AgentId { get; init; }
    public string? ConversationId { get; init; }
}