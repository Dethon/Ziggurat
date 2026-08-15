namespace Domain.DTOs.Metrics.Enums;

// Expired and Deregistered are both the machine going away, and they are kept apart because the
// difference is whether anybody meant it: one is a laptop that was switched off, the other is a
// laptop that lost its network.
public enum OutpostLifecycle
{
    Registered,
    Refreshed,
    Deregistered,
    Expired
}