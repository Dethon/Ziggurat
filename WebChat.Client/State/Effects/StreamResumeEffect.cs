using WebChat.Client.Contracts;
using WebChat.Client.Extensions;
using WebChat.Client.Models;
using WebChat.Client.Services.Streaming;
using WebChat.Client.State.Streaming;
using WebChat.Client.State.Topics;

namespace WebChat.Client.State.Effects;

public sealed class StreamResumeEffect : IDisposable
{
    private readonly TopicStreams _topicStreams;
    private readonly IStreamResumeService _streamResumeService;
    private readonly ILogger<StreamResumeEffect> _logger;
    private readonly IDisposable _startedRegistration;
    private readonly IDisposable _addedRegistration;
    private readonly IDisposable _updatedRegistration;

    // Streams reported started on topics whose rows this client is not holding yet. The push
    // that reports them also triggers the refresh that upserts the row, so the resume waits
    // for that arrival instead of dying with the lookup.
    private readonly HashSet<string> _pendingTopicIds = [];

    public StreamResumeEffect(
        Dispatcher dispatcher,
        TopicsStore topicsStore,
        TopicStreams topicStreams,
        IStreamResumeService streamResumeService,
        ILogger<StreamResumeEffect> logger)
    {
        _topicStreams = topicStreams;
        _streamResumeService = streamResumeService;
        _logger = logger;

        _startedRegistration = dispatcher.RegisterHandler<RemoteStreamStarted>(action =>
        {
            // One already resuming would be resumed twice.
            if (topicStreams.Snapshot(action.TopicId).IsResuming)
            {
                return;
            }

            var topic = topicsStore.State.Topics.FirstOrDefault(t => t.TopicId == action.TopicId);
            if (topic is null)
            {
                _pendingTopicIds.Add(action.TopicId);
                return;
            }

            Resume(topic);
        });

        // The row a pending stream was waiting for arrives as an upsert — the refresh's update
        // for a bumped row, or the add for one just created.
        _addedRegistration = dispatcher.RegisterHandler<AddTopic>(action => ResumeIfPending(action.Topic));
        _updatedRegistration = dispatcher.RegisterHandler<UpdateTopic>(action => ResumeIfPending(action.Topic));
    }

    private void ResumeIfPending(StoredTopic topic)
    {
        if (!_pendingTopicIds.Remove(topic.TopicId) || _topicStreams.Snapshot(topic.TopicId).IsResuming)
        {
            return;
        }

        Resume(topic);
    }

    // Detached on purpose: a resumed stream is long-lived, so awaiting it would mean awaiting
    // the conversation.
    private void Resume(StoredTopic topic) =>
        _streamResumeService.TryResumeStreamAsync(topic).LogFaults(_logger, nameof(RemoteStreamStarted));

    public void Dispose()
    {
        _startedRegistration.Dispose();
        _addedRegistration.Dispose();
        _updatedRegistration.Dispose();
    }
}
