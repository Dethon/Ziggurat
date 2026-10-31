using Domain.DTOs.Metrics.Enums;

namespace Domain.DTOs.Metrics;

// One thing that happened to one outpost's tunnel. Together these answer the question the metric
// exists for — "was that machine up at two o'clock" — which nothing else can answer, because an
// outpost leaves no other trace of having been there.
//
// The household whose credential opened the tunnel is the event's TenantId: two households may each
// run a laptop.
public record OutpostEvent : MetricEvent
{
    // The machine's NAME, as its mount is addressed (outpost:<NAME>).
    public required string Outpost { get; init; }

    public required OutpostLifecycle Lifecycle { get; init; }

    // Why the tunnel closed, as the relay recorded it. Absent on an opening.
    public string? Reason { get; init; }
}