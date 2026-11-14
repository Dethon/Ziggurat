using WebChat.Client.Contracts;
using WebChat.Client.Extensions;
using WebChat.Client.State.Space;
using WebChat.Client.State.Topics;

namespace WebChat.Client.State.Effects;

// The conversation on screen is remembered for the window, and the app loaded again in it opens it
// again. The agent's login link, the account page and sign-in are full loads away from the app, and an
// installed app has no address bar to say where it was: coming back landed on the list, away from the
// conversation the agent's reply was arriving in. Only the first list the app loads reopens anything,
// so a later reload of the list never takes the person somewhere they did not ask to go.
public sealed class OpenTopicEffect : IDisposable
{
    private readonly Dispatcher _dispatcher;
    private readonly TopicsStore _topics;
    private readonly SpaceStore _space;
    private readonly IOpenTopicMemory _memory;
    private readonly IDisposable _selected;
    private readonly IDisposable _loaded;
    private int _reopened;

    public OpenTopicEffect(
        Dispatcher dispatcher, TopicsStore topics, SpaceStore space, IOpenTopicMemory memory, ILogger<OpenTopicEffect> logger)
    {
        _dispatcher = dispatcher;
        _topics = topics;
        _space = space;
        _memory = memory;
        _selected = dispatcher.RegisterHandler<SelectTopic>(action =>
            _memory.RememberAsync(_space.State.CurrentSlug, action.TopicId).AsTask().LogFaults(logger, nameof(SelectTopic)));
        _loaded = dispatcher.RegisterHandler<TopicsLoaded>(action =>
            ReopenAsync(action).LogFaults(logger, nameof(TopicsLoaded)));
    }

    private async Task ReopenAsync(TopicsLoaded loaded)
    {
        if (Interlocked.Exchange(ref _reopened, 1) == 1)
        {
            return;
        }

        if (_topics.State.SelectedTopicId is not null
            || await _memory.RecallAsync(_space.State.CurrentSlug) is not { } topicId
            || loaded.Topics.All(t => t.TopicId != topicId))
        {
            return;
        }

        _dispatcher.Dispatch(new SelectTopic(topicId));
    }

    public void Dispose()
    {
        _selected.Dispose();
        _loaded.Dispose();
    }
}