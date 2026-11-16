using System.Collections.Immutable;
using Domain.DTOs.WebChat;
using Domain.Extensions;

namespace WebChat.Client.State.Queue;

// The hub told the page a topic's queue as it is now.
public record QueueChanged(QueueChangedNotification Notification) : IAction;

// A topic's queue as the hub last told it, with the revision it was read at.
public sealed record TopicQueue(IReadOnlyList<QueuedMessageItem> Items, long Revision);

public sealed record QueueState(ImmutableDictionary<string, TopicQueue> ByTopic)
{
    public static QueueState Initial => new(ImmutableDictionary<string, TopicQueue>.Empty);
}

// One queued message as the composer shows it. SenderName only for somebody else's: a page labels
// other people's messages, and offers its own person theirs.
public sealed record QueueChip(string Id, string Excerpt, int FileCount, string? SenderName, bool Mine);

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
                item.SenderId == readerId))]
            : [];
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
            state with { ByTopic = state.ByTopic.SetItem(told.TopicId, new TopicQueue(told.Items, told.Revision)) },
        _ => state
    };
}