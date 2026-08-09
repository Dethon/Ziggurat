using System.Collections.Immutable;
using Domain.DTOs.Channel;

namespace WebChat.Client.State.Streaming;

public record StreamStarted(string TopicId) : IAction;

// The server pushed a stream start. StreamResumeEffect turns it into either a resume or a
// plain StreamStarted; nothing else should react to it.
public record RemoteStreamStarted(string TopicId) : IAction;

public record StreamChunk(
    string TopicId,
    string? Content,
    string? Reasoning,
    string? ToolCalls,
    string? MessageId) : IAction;

public record StreamCompleted(string TopicId) : IAction;

public record ResetStreamingContent(string TopicId) : IAction;

// Attachments are normally read from the composer at send time; they ride the action only when
// the caller already knows them and the composer no longer does — a retry of a message that was
// sent, and cleared, before it failed.
public record SendMessage(
    string? TopicId,
    string Message,
    IReadOnlyList<AttachmentReference>? Attachments = null) : IAction;

public record CancelStreaming(string TopicId) : IAction;

public sealed class StreamingStore : IDisposable
{
    private readonly Store<StreamingState> _store;

    public StreamingStore(Dispatcher dispatcher)
    {
        _store = new Store<StreamingState>(StreamingState.Initial);

        dispatcher.RegisterCatchAll(action => _store.Dispatch(action, Reduce));
    }

    public StreamingState State => _store.State;

    public IObservable<StreamingState> StateObservable => _store.StateObservable;

    public void Dispose() => _store.Dispose();

    private static StreamingState Reduce(StreamingState state, IAction action) => action switch
    {
        StreamStarted a => state with
        {
            StreamingTopics = state.StreamingTopics.Add(a.TopicId),
            StreamingByTopic = state.StreamingByTopic.SetItem(a.TopicId, new StreamingContent())
        },

        StreamChunk a => state with
        {
            StreamingByTopic = UpdateStreamingContent(state.StreamingByTopic, a)
        },

        StreamCompleted a => RemoveStreaming(state, a.TopicId),

        ResetStreamingContent a => state with
        {
            StreamingByTopic = state.StreamingByTopic.SetItem(a.TopicId, new StreamingContent())
        },

        _ => state
    };

    private static ImmutableDictionary<string, StreamingContent> UpdateStreamingContent(
        ImmutableDictionary<string, StreamingContent> streamingByTopic,
        StreamChunk chunk)
    {
        var existing = streamingByTopic.GetValueOrDefault(chunk.TopicId) ?? new StreamingContent();

        // StreamChunk contains the FULL accumulated content from the service,
        // so we replace (not accumulate) the state
        var updated = existing with
        {
            Content = chunk.Content ?? existing.Content,
            Reasoning = chunk.Reasoning ?? existing.Reasoning,
            ToolCalls = chunk.ToolCalls ?? existing.ToolCalls,
            CurrentMessageId = chunk.MessageId ?? existing.CurrentMessageId
        };

        return streamingByTopic.SetItem(chunk.TopicId, updated);
    }

    private static StreamingState RemoveStreaming(StreamingState state, string topicId)
    {
        return state with
        {
            StreamingTopics = state.StreamingTopics.Remove(topicId),
            StreamingByTopic = state.StreamingByTopic.Remove(topicId)
        };
    }
}