using System.Collections.Immutable;
using Domain.DTOs.Channel;
using WebChat.Client.Contracts;
using WebChat.Client.Extensions;
using WebChat.Client.State.AgentActivity;
using WebChat.Client.State.Space;
using WebChat.Client.State.Streaming;
using WebChat.Client.State.Topics;

namespace WebChat.Client.State.Effects;

public sealed class AgentActivityEffect : IDisposable
{
    private readonly Dispatcher _dispatcher;
    private readonly TopicsStore _topicsStore;
    private readonly AgentActivityStore _activityStore;
    private readonly ITopicService _topicService;
    private readonly SpaceStore _spaceStore;
    private readonly IDisposable _streamingSubscription;
    private readonly IDisposable _setAgentsRegistration;
    private readonly IDisposable _selectAgentRegistration;
    private ImmutableHashSet<string> _previousStreamingTopics = [];

    public AgentActivityEffect(
        Dispatcher dispatcher,
        TopicsStore topicsStore,
        StreamingStore streamingStore,
        AgentActivityStore activityStore,
        ITopicService topicService,
        SpaceStore spaceStore,
        ILogger<AgentActivityEffect> logger)
    {
        _dispatcher = dispatcher;
        _topicsStore = topicsStore;
        _activityStore = activityStore;
        _topicService = topicService;
        _spaceStore = spaceStore;

        _setAgentsRegistration = dispatcher.RegisterHandler<SetAgents>(
            action => MapAllAgentTopicsAsync(action.Agents).LogFaults(logger, nameof(SetAgents)));
        _selectAgentRegistration = dispatcher.RegisterHandler<SelectAgent>(
            action => ClearUnseenActivity(action.AgentId));

        // Stays observable-driven: the activity mapping this feeds is derived from streaming
        // state, and there is no action that means "a stream finished for another agent".
        _streamingSubscription = streamingStore.StateObservable.Subscribe(HandleStreamingChange);
    }

    public void ClearUnseenActivity(string agentId) =>
        _dispatcher.Dispatch(new ClearAgentUnseenActivity(agentId));

    public async Task MapAllAgentTopicsAsync(IReadOnlyList<AgentCatalogEntry> agents)
    {
        var slug = _spaceStore.State.CurrentSlug;

        // One page per agent, not every topic each has ever had. The indicator is about what is
        // busy now, and anything busy is by definition recently written to, so it is on the
        // first page.
        var fetches = await Task.WhenAll(agents.Select(async agent =>
            (Agent: agent, Page: await _topicService.GetTopicPageAsync(agent.Id, slug))));

        // Seeded with what we already know: an agent whose read failed keeps its last-known
        // mapping instead of losing it because a sibling agent's read succeeded. Only the
        // agents that answered get their entries replaced with the fresh read.
        var map = new Dictionary<string, string>(_activityStore.State.TopicToAgent);
        foreach (var (agent, page) in fetches.Where(fetch => fetch.Page.IsLive))
        {
            foreach (var staleTopicId in map
                .Where(pair => pair.Value == agent.Id)
                .Select(pair => pair.Key)
                .ToList())
            {
                map.Remove(staleTopicId);
            }

            foreach (var topic in page.Value!.Topics)
            {
                map[topic.TopicId] = agent.Id;
            }
        }

        _dispatcher.Dispatch(new AllAgentsTopicsMapped(map));
    }

    private void HandleStreamingChange(StreamingState state)
    {
        var completed = _previousStreamingTopics.Except(state.StreamingTopics);
        var selectedAgent = _topicsStore.State.SelectedAgentId;
        var map = _activityStore.State.TopicToAgent;

        foreach (var topicId in completed.Where(t => map.TryGetValue(t, out var a) && a != selectedAgent))
        {
            _dispatcher.Dispatch(new MarkAgentUnseenActivity(map[topicId]));
        }

        _previousStreamingTopics = state.StreamingTopics;
    }

    public void Dispose()
    {
        _streamingSubscription.Dispose();
        _setAgentsRegistration.Dispose();
        _selectAgentRegistration.Dispose();
    }
}