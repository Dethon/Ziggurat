namespace Domain.DTOs.Metrics.Enums;

// Persisted as integers in metric events (Redis): pin values explicitly, never renumber or reuse.
// See VoiceMetric for the rationale; guarded by ModalDismissalEnumsTests.
public enum ModalDismissalDimension
{
    Kind = 0,
    Outcome = 1
}