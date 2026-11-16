using System.Collections.Immutable;
using Domain.DTOs.WebChat;
using Domain.Extensions;

namespace WebChat.Client.State.Queue;

// The whole queue rather than what changed in it, so a page that missed a push is set right by the next.
public record QueueChanged(QueueChangedNotification Notification) : IAction;

// Only that there are some: the hub hands them to the first of the person's pages to ask, so they land
// in one composer rather than in every page the person has open.
public record QueueReturned(string TopicId) : IAction;

// Taken back rather than changed where it waits: the hub never rewrites a queued message, so the
// reworked words go out again as a new one.
public record EditQueuedMessage(string TopicId, string QueuedId) : IAction;

public record RemoveQueuedMessage(string TopicId, string QueuedId) : IAction;

// Steered only into the reader's own running turn: the agent cuts a turn for the person it acts for alone.
public record SteerQueuedMessage(string TopicId, string QueuedId) : IAction;

// The hub let go of a message this page withdrew: its chip goes now, ahead of the hub's own telling,
// so a second press does not ask again about a message already withdrawn.
public record QueuedMessageWithdrawn(string TopicId, string QueuedId) : IAction;

// A topic's queue as the hub last told it, with the revision it was read at, and whose own turn was
// running then (by user id): the one person Steer now is offered to.
public sealed record TopicQueue(IReadOnlyList<QueuedMessageItem> Items, long Revision, string? RunningFor = null);

public sealed record QueueState(ImmutableDictionary<string, TopicQueue> ByTopic)
{
    public static QueueState Initial => new(ImmutableDictionary<string, TopicQueue>.Empty);
}

// What a chip offers the reader: a message is withdrawn by its sender alone, and steered with only into
// a running turn of their own.
[Flags]
public enum QueueChipActions
{
    None = 0,
    Edit = 1,
    Remove = 2,
    Steer = 4
}

// One queued message as the composer shows it. SenderName only for somebody else's: a page labels
// other people's messages, and offers its own person theirs.
public sealed record QueueChip(
    string Id, string Excerpt, int FileCount, string? SenderName, bool Mine, QueueChipActions Actions = QueueChipActions.None);

public static class QueueSelectors
{
    // A chip is a line: past this the words are cut at a word.
    private const int ExcerptLength = 80;

    public static IReadOnlyList<QueueChip> Chips(QueueState state, string? topicId, string? readerId) =>
        topicId is not null && state.ByTopic.TryGetValue(topicId, out var queue)
            ? [.. queue.Items.Select(item => new QueueChip(
                item.Id,
                item.Content.Excerpt(ExcerptLength),
                item.FileCount,
                item.SenderId == readerId ? null : item.SenderName,
                item.SenderId == readerId,
                ActionsOn(item, queue, readerId)))]
            : [];

    private static QueueChipActions ActionsOn(QueuedMessageItem item, TopicQueue queue, string? readerId) =>
        item.SenderId != readerId ? QueueChipActions.None
        : queue.RunningFor == readerId ? QueueChipActions.Edit | QueueChipActions.Remove | QueueChipActions.Steer
        : QueueChipActions.Edit | QueueChipActions.Remove;
}

public sealed class QueueStore : IDisposable
{
    private readonly Store<QueueState> _store;

    public QueueStore(Dispatcher dispatcher)
    {
        _store = new Store<QueueState>(QueueState.Initial);
        dispatcher.RegisterCatchAll(action => _store.Dispatch(action, Reduce));
    }

    public QueueState State => _store.State;
    public IObservable<QueueState> StateObservable => _store.StateObservable;
    public void Dispose() => _store.Dispose();

    // Two pushes sent close together may arrive the other way round, so the one read later wins
    // whichever lands last.
    private static QueueState Reduce(QueueState state, IAction action) => action switch
    {
        QueueChanged { Notification: var told }
            when !state.ByTopic.TryGetValue(told.TopicId, out var shown) || told.Revision > shown.Revision =>
            state with
            {
                ByTopic = state.ByTopic.SetItem(told.TopicId, new TopicQueue(told.Items, told.Revision, told.RunningFor))
            },
        QueuedMessageWithdrawn { TopicId: var topicId, QueuedId: var queuedId }
            when state.ByTopic.TryGetValue(topicId, out var shown) =>
            state with
            {
                ByTopic = state.ByTopic.SetItem(
                    topicId, shown with { Items = [.. shown.Items.Where(item => item.Id != queuedId)] })
            },
        _ => state
    };
}