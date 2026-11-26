using Domain.DTOs.Metrics;
using Domain.DTOs.Metrics.Enums;

namespace Dashboard.Client.State.Voice;

public record VoiceState
{
    public IReadOnlyList<VoiceEvent> Events { get; init; } = [];
    public VoiceDimension GroupBy { get; init; } = VoiceDimension.SatelliteId;
    public VoiceMetric Metric { get; init; } = VoiceMetric.UtteranceTranscribed;
    public Dictionary<string, decimal> Breakdown { get; init; } = [];
    // The last how many days the page reads: a rolling window the server resolves on every read.
    public int Days { get; init; } = 1;
    public Aggregation Agg { get; init; } = Aggregation.Avg;
}