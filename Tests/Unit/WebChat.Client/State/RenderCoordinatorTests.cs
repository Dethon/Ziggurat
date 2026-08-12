using Shouldly;
using WebChat.Client.State;
using WebChat.Client.State.Streaming;

namespace Tests.Unit.WebChat.Client.State;

public class RenderCoordinatorTests : IDisposable
{
    private readonly Dispatcher _dispatcher;
    private readonly StreamingStore _streamingStore;
    private readonly RenderCoordinator _coordinator;

    public RenderCoordinatorTests()
    {
        _dispatcher = new Dispatcher();
        _streamingStore = new StreamingStore(_dispatcher);
        _coordinator = new RenderCoordinator(_streamingStore);
    }

    public void Dispose()
    {
        _coordinator.Dispose();
        _streamingStore.Dispose();
    }

    [Fact]
    public async Task CreateStreamingObservable_EmitsNull_WhenTopicNotStreaming()
    {
        StreamingContent? received = null;
        var observable = _coordinator.CreateStreamingObservable("topic-1");

        using var subscription = observable.Subscribe(value => received = value);

        // Nothing is expected, so there is no state to wait for — only time in which the wrong
        // value could show up. Sleeping the thread rather than the task took a pool thread out of
        // a suite that runs twenty-four at once for as long as it lasted.
        await Eventually.Settle();

        received.ShouldBeNull();
    }

    [Fact]
    public async Task CreateStreamingObservable_DoesNotEmitDuplicates()
    {
        var received = new List<StreamingContent?>();
        var gate = new Lock();
        var observable = _coordinator.CreateStreamingObservable("topic-1");

        using var subscription = observable.Subscribe(value =>
        {
            lock (gate)
            {
                received.Add(value);
            }
        });

        _dispatcher.Dispatch(new StreamStarted("topic-1"));
        _dispatcher.Dispatch(new StreamChunk("topic-1", "Hello", null, null, "msg-1"));

        // The stream is sampled every 50ms, so the emission is not there the instant the chunk is
        // dispatched. Sleeping three intervals and counting assumed the first one had landed by
        // then, and on a machine running the whole suite at once it had not — the count came back
        // zero and read as a duplicate-suppression failure, which is the opposite of what happened.
        //
        // Wait for the first emission to arrive, then keep watching well past it: what this pins is
        // that a second one never comes, and only the waiting after the first proves that.
        int helloCount()
        {
            lock (gate)
            {
                return received.Count(c => c?.Content == "Hello");
            }
        }

        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (helloCount() == 0 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }

        await Task.Delay(200);

        helloCount().ShouldBe(1);
    }

    [Fact]
    public async Task CreateIsStreamingObservable_ReturnsCorrectBoolean()
    {
        var gate = new Lock();
        var received = new List<bool>();
        var observable = _coordinator.CreateIsStreamingObservable("topic-1");

        using var subscription = observable.Subscribe(value =>
        {
            lock (gate)
            {
                received.Add(value);
            }
        });

        bool Saw(bool value)
        {
            lock (gate)
            {
                return received.Contains(value);
            }
        }

        // The stream is sampled, so an emission is not there the instant the state changes. Two
        // sample intervals of sleep assumed the first tick had landed inside them, and on a machine
        // running the whole suite at once it had not.
        await Eventually.Until(() => Saw(false), "the idle topic to report not-streaming");

        _dispatcher.Dispatch(new StreamStarted("topic-1"));

        await Eventually.Until(() => Saw(true), "the started stream to report streaming");
    }

    [Fact]
    public async Task CreateIsStreamingObservable_UpdatesWhenStreamCompletes()
    {
        var gate = new Lock();
        var received = new List<bool>();
        var observable = _coordinator.CreateIsStreamingObservable("topic-1");

        using var subscription = observable.Subscribe(value =>
        {
            lock (gate)
            {
                received.Add(value);
            }
        });

        bool Saw(bool value)
        {
            lock (gate)
            {
                return received.Contains(value);
            }
        }

        // Waiting for the started stream to be sampled before completing it is what makes this a
        // test of the transition: complete it too early and the sampler only ever sees the finished
        // state, so the true never arrives and the failure names the wrong half.
        _dispatcher.Dispatch(new StreamStarted("topic-1"));
        await Eventually.Until(() => Saw(true), "the started stream to be sampled as streaming");

        _dispatcher.Dispatch(new StreamCompleted("topic-1"));
        await Eventually.Until(() => Saw(false), "the completed stream to be sampled as finished");
    }
}