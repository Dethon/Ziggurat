using Domain.DTOs.Metrics;
using Infrastructure.Metrics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Shouldly;

namespace Tests.Unit.Infrastructure.Metrics;

public class BufferedMetricsPublisherTests
{
    private sealed class FakeSink : IMetricSink
    {
        private readonly List<MetricEvent> _events = [];
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource? Gate { get; set; }
        public Func<MetricEvent, Exception?> ThrowFor { get; set; } = _ => null;

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

        // Signals that the drain has taken an event off the channel, so a test can say exactly how
        // many are still buffered behind it.
        public Task Entered => _entered.Task;

        public async Task SendAsync(MetricEvent metricEvent, CancellationToken ct = default)
        {
            _entered.TrySetResult();

            if (Gate is not null)
            {
                await Gate.Task;
            }

            if (ThrowFor(metricEvent) is { } ex)
            {
                throw ex;
            }

            lock (_events)
            {
                _events.Add(metricEvent);
            }
        }
    }

    private static ErrorEvent Event(string msg = "m") =>
        new() { Service = "test", ErrorType = "t", Message = msg };

    [Fact]
    public async Task Publish_SinkBlocked_ReturnsWithoutWaiting()
    {
        var sink = new FakeSink
        {
            Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)
        };
        await using var publisher = new BufferedMetricsPublisher(sink);

        // Publish is void, so "did not wait" is only observable as the sink not having run yet
        // while the gate is still closed.
        publisher.Publish(Event());

        sink.Events.ShouldBeEmpty();
        sink.Gate.TrySetResult();
    }

    [Fact]
    public async Task Publish_SinkThrowsOnOneEvent_LaterEventsStillDrain()
    {
        var sink = new FakeSink
        {
            ThrowFor = e => e is ErrorEvent { Message: "poison" }
                ? new InvalidOperationException("redis down")
                : null
        };
        var publisher = new BufferedMetricsPublisher(sink);

        publisher.Publish(Event("poison"));
        publisher.Publish(Event("after"));

        await publisher.DisposeAsync();

        sink.Events.ShouldHaveSingleItem().ShouldBeOfType<ErrorEvent>().Message.ShouldBe("after");
    }

    // Dropping at capacity is the one loss nothing downstream can recover, so it must not be
    // silent — the warning is the only trace an operator gets.
    [Fact]
    public async Task Publish_BufferFull_DropsWithAWarningRatherThanThrowingOrBlocking()
    {
        var sink = new FakeSink
        {
            Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)
        };
        using var logs = CapturingLoggerProvider.ForLevel(LogLevel.Warning);
        var logger = new LoggerFactory([logs]).CreateLogger<BufferedMetricsPublisher>();
        await using var publisher = new BufferedMetricsPublisher(sink, logger, capacity: 1);

        // The drain may have taken the first event and parked on the gate, so fill well past
        // capacity: every write beyond it drops rather than throwing or waiting for room.
        Should.NotThrow(() => Enumerable.Range(0, 10).ToList().ForEach(i => publisher.Publish(Event($"e{i}"))));

        logs.Messages.ShouldContain(m => m.Contains("Metrics buffer full") && m.Contains(nameof(ErrorEvent)));
        sink.Gate.TrySetResult();
    }

    [Fact]
    public async Task DisposeAsync_FlushesPendingEvents()
    {
        var sink = new FakeSink();
        var publisher = new BufferedMetricsPublisher(sink);
        Enumerable.Range(0, 100).ToList().ForEach(i => publisher.Publish(Event($"e{i}")));

        await publisher.DisposeAsync();

        sink.Events.Count.ShouldBe(100);
    }

    // Disposal gives the drain five seconds and then leaves. Whatever is still buffered is gone for
    // good, so walking away from it silently is the same irrecoverable loss as dropping at capacity.
    [Fact]
    public async Task DisposeAsync_TheDrainDoesNotFinishInTime_SaysHowManyEventsWereLost()
    {
        var sink = new FakeSink
        {
            Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)
        };
        using var logs = new CapturingLoggerProvider(LogLevel.Warning);
        var logger = new LoggerFactory([logs]).CreateLogger<BufferedMetricsPublisher>();
        var time = new FakeTimeProvider();
        var publisher = new BufferedMetricsPublisher(sink, logger, timeProvider: time);
        Enumerable.Range(0, 5).ToList().ForEach(i => publisher.Publish(Event($"e{i}")));
        await sink.Entered;

        var disposing = publisher.DisposeAsync();
        time.Advance(TimeSpan.FromSeconds(5));
        await disposing;

        // One event is parked in the sink, so four never left the buffer.
        logs.Messages.ShouldContain(m => m.Contains("Metrics drain abandoned") && m.Contains("4"));
        sink.Gate.TrySetResult();
    }

    // The reader is the only thing that ever empties the channel: if it dies, Publish carries on
    // accepting into a buffer nobody reads. The publish failure path is what runs on every event,
    // so a logger that throws there used to take the reader down with it, and in silence.
    [Fact]
    public async Task DrainAsync_TheLoopThrowsOutsideTheSink_SaysTheDrainStopped()
    {
        var sink = new FakeSink { ThrowFor = _ => new InvalidOperationException("redis down") };
        var logger = new ThrowOnFirstCallLogger();
        var publisher = new BufferedMetricsPublisher(sink, logger);

        publisher.Publish(Event());
        await publisher.DisposeAsync();

        logger.Messages.ShouldContain(m => m.Contains("Metrics drain stopped"));
    }

    private sealed class ThrowOnFirstCallLogger : ILogger<BufferedMetricsPublisher>
    {
        private int _calls;

        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (Interlocked.Increment(ref _calls) == 1)
            {
                throw new InvalidOperationException("logging is broken");
            }

            Messages.Add(formatter(state, exception));
        }
    }
}