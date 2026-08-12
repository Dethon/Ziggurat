using Domain.DTOs.Channel;
using WebChat.Client.Models;

namespace WebChat.Client.State.Topics;

public record LoadTopics : IAction;

// The first page of a list being started over: a first load, an agent change, or catch-up after
// an interruption. Everything held is replaced, cursor included.
public record TopicsLoaded(IReadOnlyList<StoredTopic> Topics, string? NextCursor = null) : IAction;

// The command: fetch the page below the cursor. Ignored while one is already in flight or the
// range has ended.
public record LoadMoreTopics : IAction;

public record TopicsPageAppended(IReadOnlyList<StoredTopic> Topics, string? NextCursor) : IAction;

public record SelectTopic(string? TopicId) : IAction;

public record AddTopic(StoredTopic Topic) : IAction;

public record UpdateTopic(StoredTopic Topic) : IAction;

// The command: ask for a rename. The row keeps its old name until the server took the new one,
// for the same reason a deleted row stays until the delete was made.
public record RenameTopic(string TopicId, string Name) : IAction;

// The command: ask for a delete. The row itself only leaves on TopicRemoved, once the
// server confirmed — so a delete that could not be made never touches the sidebar.
public record RemoveTopic(string TopicId, string? AgentId = null, long? ChatId = null, long? ThreadId = null) : IAction;

public record TopicRemoved(string TopicId) : IAction;

public record SetAgents(IReadOnlyList<AgentCatalogEntry> Agents) : IAction;

public record SelectAgent(string AgentId) : IAction;

public record TopicsError(string Message) : IAction;

public record CreateNewTopic : IAction;

public record Initialize : IAction;

public sealed class TopicsStore : IDisposable
{
    private readonly Store<TopicsState> _store;

    public TopicsStore(Dispatcher dispatcher)
    {
        _store = new Store<TopicsState>(TopicsState.Initial);

        dispatcher.RegisterCatchAll(action => _store.Dispatch(action, Reduce));
    }

    public TopicsState State => _store.State;

    public IObservable<TopicsState> StateObservable => _store.StateObservable;

    public void Dispose() => _store.Dispose();

    private static TopicsState Reduce(TopicsState state, IAction action) => action switch
    {
        LoadTopics => state with
        {
            IsLoading = true,
            Error = null
        },

        TopicsLoaded a => state with
        {
            Paging = TopicPaging.FirstPage(a.Topics, a.NextCursor),
            IsLoading = false,
            Error = null
        },

        TopicsPageAppended a => state with
        {
            Paging = state.Paging.AppendPage(a.Topics, a.NextCursor),
            IsLoading = false,
            Error = null
        },

        SelectTopic a => state with
        {
            SelectedTopicId = a.TopicId
        },

        AddTopic a => state with
        {
            Paging = state.Paging.Insert(a.Topic),
            Error = null
        },

        UpdateTopic a => state with
        {
            Paging = state.Paging.Upsert(a.Topic),
            Error = null
        },

        TopicRemoved a => state with
        {
            Paging = state.Paging.Remove(a.TopicId),
            SelectedTopicId = state.SelectedTopicId == a.TopicId ? null : state.SelectedTopicId,
            Error = null
        },

        // A live catalog refresh may drop the selected agent; fall back to the first available
        // (or null when empty) so the UI never points at a ghost agent. The selected topic
        // belonged to the dropped agent, so it goes with it, exactly as SelectAgent does —
        // the new agent's topics load right after, and a topic id that survives that load is
        // one every later send fails to find.
        SetAgents a when state.SelectedAgentId is not null && a.Agents.All(ag => ag.Id != state.SelectedAgentId) =>
            state with
            {
                Agents = a.Agents,
                SelectedAgentId = a.Agents.FirstOrDefault()?.Id,
                SelectedTopicId = null,
                Error = null
            },

        SetAgents a => state with
        {
            Agents = a.Agents,
            Error = null
        },

        SelectAgent a => state with
        {
            SelectedAgentId = a.AgentId,
            SelectedTopicId = null
        },

        TopicsError a => state with
        {
            Error = a.Message,
            IsLoading = false
        },

        CreateNewTopic => state with
        {
            SelectedTopicId = null
        },

        _ => state
    };
}