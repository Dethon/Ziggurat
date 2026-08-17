using Domain.DTOs.Metrics.Enums;

namespace Domain.DTOs.Metrics;

// One thing that happened to one outpost registration. Together these answer the question the
// metric exists for — "was that machine up at two o'clock" — which nothing else can answer,
// because an outpost leaves no other trace of having been there.
public record OutpostEvent : MetricEvent
{
    public required string Outpost { get; init; }
    public required OutpostLifecycle Lifecycle { get; init; }

    // The address the machine registered. Absent on an expiry: the entry carrying it is the thing
    // that has gone.
    public string? Endpoint { get; init; }
}