using WebChat.Client.Models;

namespace WebChat.Client.State.Messages;

public record MessagesLoaded(string TopicId, IReadOnlyList<ChatMessageModel> Messages) : IAction;

public record AddMessage(string TopicId, ChatMessageModel Message, string? StreamMessageId = null) : IAction;

public record UpdateMessage(string TopicId, string MessageId, ChatMessageModel Message) : IAction;

public record ClearMessages(string TopicId) : IAction;

public record RemoveTrailingErrors(string TopicId) : IAction;

// The message a retry sends again, taken back so the retry stands in its place.
public record RemoveLastMessage(string TopicId) : IAction;

public record RetryLastMessage(string TopicId) : IAction;

public record ClearAllMessages : IAction;

public sealed class MessagesStore : IDisposable
{
    private readonly Store<MessagesState> _store;

    public MessagesStore(Dispatcher dispatcher)
    {
        _store = new Store<MessagesState>(MessagesState.Initial);

        dispatcher.RegisterCatchAll(action => _store.Dispatch(action, Reduce));
    }

    public MessagesState State => _store.State;

    public IObservable<MessagesState> StateObservable => _store.StateObservable;

    public void Dispose() => _store.Dispose();

    private static MessagesState Reduce(MessagesState state, IAction action) => action switch
    {
        MessagesLoaded a => state with
        {
            MessagesByTopic = state.MessagesByTopic.With(a.TopicId, a.Messages),
            LoadedTopics = new HashSet<string>(state.LoadedTopics) { a.TopicId },
            FinalizedMessageIdsByTopic = state.FinalizedMessageIdsByTopic.With(
                a.TopicId,
                (IReadOnlySet<string>)a.Messages
                    .Select(m => m.MessageId)
                    .Where(id => id is not null)
                    .ToHashSet()!)
        },

        AddMessage a => AddMessageWithDedup(state, a.TopicId, a.Message, a.StreamMessageId),

        UpdateMessage a => state with
        {
            MessagesByTopic = UpdateMessageInTopic(state.MessagesByTopic, a.TopicId, a.MessageId, a.Message)
        },

        RemoveTrailingErrors a when state.MessagesByTopic.TryGetValue(a.TopicId, out var msgs) =>
            state with
            {
                MessagesByTopic = state.MessagesByTopic.With(
                    a.TopicId, msgs.Reverse().SkipWhile(m => m.IsError).Reverse().ToList())
            },

        RemoveTrailingErrors => state,

        RemoveLastMessage a when state.MessagesByTopic.TryGetValue(a.TopicId, out var msgs) && msgs.Count > 0 =>
            state with { MessagesByTopic = state.MessagesByTopic.With(a.TopicId, msgs.Take(msgs.Count - 1).ToList()) },

        RemoveLastMessage => state,

        ClearMessages a => state with
        {
            MessagesByTopic = state.MessagesByTopic.Without(a.TopicId),
            LoadedTopics = new HashSet<string>(state.LoadedTopics.Where(t => t != a.TopicId)),
            FinalizedMessageIdsByTopic = state.FinalizedMessageIdsByTopic.Without(a.TopicId)
        },

        ClearAllMessages => MessagesState.Initial,

        _ => state
    };

    private static IReadOnlyDictionary<string, IReadOnlyList<ChatMessageModel>> UpdateMessageInTopic(
        IReadOnlyDictionary<string, IReadOnlyList<ChatMessageModel>> messagesByTopic,
        string topicId,
        string messageId,
        ChatMessageModel updatedMessage)
    {
        if (!messagesByTopic.TryGetValue(topicId, out var messages))
        {
            return messagesByTopic;
        }

        var updated = messages
            .Select(m => m.MessageId == messageId ? updatedMessage : m)
            .ToList();

        if (updated.SequenceEqual(messages))
        {
            return messagesByTopic;
        }

        return messagesByTopic.With(topicId, (IReadOnlyList<ChatMessageModel>)updated);
    }

    private static MessagesState AddMessageWithDedup(
        MessagesState state,
        string topicId,
        ChatMessageModel message,
        string? streamMessageId)
    {
        var existingMessages = state.MessagesByTopic.GetValueOrDefault(topicId, []);
        var finalizedIds = state.FinalizedMessageIdsByTopic.GetValueOrDefault(topicId)
                           ?? new HashSet<string>();

        // If a stream message ID is provided, check if it's already been finalized
        // This prevents duplicates from race conditions between HandleUserMessage
        // and StreamingService both trying to add the same assistant message
        if (!string.IsNullOrEmpty(streamMessageId) && finalizedIds.Contains(streamMessageId))
        {
            return state;
        }

        var newFinalizedIds = finalizedIds;
        if (!string.IsNullOrEmpty(streamMessageId))
        {
            newFinalizedIds = new HashSet<string>(finalizedIds) { streamMessageId };
        }

        return state with
        {
            MessagesByTopic = state.MessagesByTopic.With(
                topicId, (IReadOnlyList<ChatMessageModel>)existingMessages.Append(message).ToList()),
            FinalizedMessageIdsByTopic = state.FinalizedMessageIdsByTopic.With(topicId, newFinalizedIds)
        };
    }
}