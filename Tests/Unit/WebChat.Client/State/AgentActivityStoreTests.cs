using Shouldly;
using WebChat.Client.State;
using WebChat.Client.State.AgentActivity;

namespace Tests.Unit.WebChat.Client.State;

public sealed class AgentActivityStoreTests : IDisposable
{
    private readonly Dispatcher _dispatcher;
    private readonly AgentActivityStore _store;

    public AgentActivityStoreTests()
    {
        _dispatcher = new Dispatcher();
        _store = new AgentActivityStore(_dispatcher);
    }

    public void Dispose() => _store.Dispose();

    [Fact]
    public void MarkAgentUnseenActivity_AddsAgent()
    {
        _dispatcher.Dispatch(new MarkAgentUnseenActivity("a2"));

        _store.State.AgentsWithUnseenActivity.ShouldContain("a2");
    }

    [Fact]
    public void ClearAgentUnseenActivity_RemovesAgent()
    {
        _dispatcher.Dispatch(new MarkAgentUnseenActivity("a2"));

        _dispatcher.Dispatch(new ClearAgentUnseenActivity("a2"));

        _store.State.AgentsWithUnseenActivity.ShouldNotContain("a2");
    }
}