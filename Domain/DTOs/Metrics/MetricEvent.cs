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
[JsonDerivedType(typeof(SkillPreloadEvent), "skill_preload")]
[JsonDerivedType(typeof(MemoryJudgmentEvent), "memory_judgment")]
[JsonDerivedType(typeof(ModalDismissalEvent), "modal_dismissal")]
[JsonDerivedType(typeof(ExecScreenEvent), "exec_screen")]
[JsonDerivedType(typeof(LemonadeUsageEvent), "lemonade_usage")]
public abstract record MetricEvent
{
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
    // The agent's id, which keys its figures, and the name it went by when this happened, which they
    // are shown under: a household renames an agent without splitting what it used.
    public string? AgentId { get; init; }
    public string? AgentName { get; init; }
    public string? ConversationId { get; init; }

    // The household server a back-call brought this event from (its synthetic address,
    // `<name>.<household>.tunnel`), stamped by the relay over whatever the server said; null for an
    // event a deployment service published itself.
    public string? Origin { get; init; }

    // The household the event happened for, which keys every roll-up it lands in (MetricKeys);
    // null is the deployment's own — a keep-alive, a relay refusal, a deployment service's
    // heartbeat. A back-call's event carries the household its tunnel's credential names, stamped
    // by the relay over whatever the server said, like Origin.
    public string? TenantId { get; init; }
}