namespace Domain.DTOs.Metrics.Enums;

// Persisted as integers in metric events (Redis): pin values explicitly, never renumber or reuse.
// See VoiceMetric for the rationale; guarded by SkillPreloadEnumsTests. A member ending in Ms is
// a duration to the query service, as in VoiceMetric.
public enum SkillPreloadMetric
{
    Count = 0,
    LatencyMs = 1,
    InputTokens = 2
}