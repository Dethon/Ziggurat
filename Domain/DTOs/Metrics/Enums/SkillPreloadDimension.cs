namespace Domain.DTOs.Metrics.Enums;

// Persisted as integers in metric events (Redis): pin values explicitly, never renumber or reuse.
// See VoiceMetric for the rationale; guarded by SkillPreloadEnumsTests.
public enum SkillPreloadDimension
{
    Outcome = 0,
    Agent = 1,
    Channel = 2,
    Skill = 3
}