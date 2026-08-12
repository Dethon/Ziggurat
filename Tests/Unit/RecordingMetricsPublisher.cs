using Domain.Contracts;
using Domain.DTOs.Metrics;

namespace Tests.Unit;

// Publishing is fire-and-forget by contract, so a test reads what was published rather than
// awaiting anything.
internal sealed class RecordingMetricsPublisher : IMetricsPublisher
{
    private readonly List<MetricEvent> _published = [];

    public IReadOnlyList<MetricEvent> Published
    {
        get
        {
            lock (_published)
            {
                return [.. _published];
            }
        }
    }

    public void Publish(MetricEvent metricEvent)
    {
        lock (_published)
        {
            _published.Add(metricEvent);
        }
    }
}