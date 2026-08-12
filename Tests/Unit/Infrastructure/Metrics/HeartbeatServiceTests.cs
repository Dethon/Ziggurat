using Domain.Contracts;
using Domain.DTOs.Metrics;
using Infrastructure.Metrics;
using Shouldly;

namespace Tests.Unit.Infrastructure.Metrics;

public class HeartbeatServiceTests
{
    private sealed class RecordingPublisher : IMetricsPublisher
    {
        private readonly List<MetricEvent> _events = [];

        public IReadOnlyList<MetricEvent> Events
        {
            get
            {
                lock (_events)
                {
                    return [.. _events];
                }
            }
        }

        public void Publish(MetricEvent metricEvent)
        {
            lock (_events)
            {
                _events.Add(metricEvent);
            }
        }

    }

    [Fact]
    public async Task ExecuteAsync_publishes_heartbeat_event_with_service_name()
    {
        var publisher = new RecordingPublisher();
        var sut = new HeartbeatService(publisher, "test-service");

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        // The heartbeat is published from the service's own loop, so fifty milliseconds was a bet
        // that the loop got a thread inside them. Stopping the service before the first beat is
        // exactly how that bet loses, and it loses as "the service never published".
        await sut.StartAsync(cts.Token);
        await Eventually.Until(
            () => publisher.Events.OfType<HeartbeatEvent>().Any(e => e.Service == "test-service"),
            "the service to publish its first heartbeat");
        await sut.StopAsync(CancellationToken.None);

        publisher.Events.OfType<HeartbeatEvent>()
            .ShouldContain(e => e.Service == "test-service");
    }
}