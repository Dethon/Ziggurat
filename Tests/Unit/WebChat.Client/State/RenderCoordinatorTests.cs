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
    public void CreateStreamingObservable_EmitsNull_WhenTopicNotStreaming()
    {
        StreamingContent? received = null;
        var observable = _coordinator.CreateStreamingObservable("topic-1");

        using var subscription = observable.Subscribe(value => received = value);

        // Wait for multiple sample intervals to ensure capture
        Thread.Sleep(120);

        received.ShouldBeNull();
    }

    [Fact]
    public void CreateStreamingObservable_DoesNotEmitDuplicates()
    {
        var received = new List<StreamingContent?>();
        var observable = _coordinator.CreateStreamingObservable("topic-1");

        using var subscription = observable.Subscribe(value => received.Add(value));

        _dispatcher.Dispatch(new StreamStarted("topic-1"));
        _dispatcher.Dispatch(new StreamChunk("topic-1", "Hello", null, null, "msg-1"));

        // Wait for multiple sample intervals
        Thread.Sleep(150);

        // DistinctUntilChanged should prevent duplicate emissions
        var helloCount = received.Count(c => c != null && c.Content == "Hello");
        helloCount.ShouldBe(1);
    }

    [Fact]
    public void CreateIsStreamingObservable_ReturnsCorrectBoolean()
    {
        var received = new List<bool>();
        var observable = _coordinator.CreateIsStreamingObservable("topic-1");

        using var subscription = observable.Subscribe(value => received.Add(value));

        // Wait for sample interval — should emit false since not streaming
        Thread.Sleep(120);
        received.ShouldContain(false);

        _dispatcher.Dispatch(new StreamStarted("topic-1"));

        // Wait for sample interval — should now emit true
        Thread.Sleep(120);
        received.ShouldContain(true);
    }

    [Fact]
    public void CreateIsStreamingObservable_UpdatesWhenStreamCompletes()
    {
        var received = new List<bool>();
        var observable = _coordinator.CreateIsStreamingObservable("topic-1");

        using var subscription = observable.Subscribe(value => received.Add(value));

        _dispatcher.Dispatch(new StreamStarted("topic-1"));
        Thread.Sleep(250);

        _dispatcher.Dispatch(new StreamCompleted("topic-1"));
        Thread.Sleep(250);

        received.ShouldContain(true);
        received.ShouldContain(false);
    }

    [Fact]
    public void CreateObservables_ThrowOnNullTopicId()
    {
        Should.Throw<ArgumentNullException>(() => _coordinator.CreateStreamingObservable(null!));
        Should.Throw<ArgumentNullException>(() => _coordinator.CreateIsStreamingObservable(null!));
    }
}