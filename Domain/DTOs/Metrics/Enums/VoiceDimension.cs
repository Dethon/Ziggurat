namespace Domain.DTOs.Metrics.Enums;

// Persisted as integers in metric events (Redis): pin values explicitly, never renumber or reuse.
// See VoiceMetric for the rationale; guarded by VoiceEnumsTests.
public enum VoiceDimension
{
    SatelliteId = 0,
    Room = 1,
    Identity = 2,
    Outcome = 3,
    Priority = 4,
    Speaker = 5,
    Channel = 6
}