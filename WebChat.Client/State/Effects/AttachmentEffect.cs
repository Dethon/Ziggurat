using System.Collections.Concurrent;
using Domain.Conversations;
using Domain.DTOs.WebChat;
using WebChat.Client.Contracts;
using WebChat.Client.Extensions;
using WebChat.Client.Models;
using WebChat.Client.State.Composer;
using WebChat.Client.State.Messages;
using WebChat.Client.State.Space;
using WebChat.Client.State.Toast;
using WebChat.Client.State.Topics;

namespace WebChat.Client.State.Effects;

// Each file uploads as soon as it is picked, so typing carries on while the network does. A file
// that is too large or of a kind this chat does not take is refused here, at pick time, rather
// than after it has crossed the wire.
public sealed class AttachmentEffect : IDisposable
{
    private readonly Dispatcher _dispatcher;
    private readonly ComposerStore _composerStore;
    private readonly TopicsStore _topicsStore;
    private readonly SpaceStore _spaceStore;
    private readonly IAttachmentService _attachmentService;
    private readonly IAttachmentUploader _uploader;
    private readonly IChatSessionService _sessionService;
    private readonly ITopicService _topicService;
    private readonly ILogger<AttachmentEffect> _logger;
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _uploads = new();
    private readonly IDisposable _attachRegistration;
    private readonly IDisposable _removeRegistration;

    public AttachmentEffect(
        Dispatcher dispatcher,
        ComposerStore composerStore,
        TopicsStore topicsStore,
        SpaceStore spaceStore,
        IAttachmentService attachmentService,
        IAttachmentUploader uploader,
        IChatSessionService sessionService,
        ITopicService topicService,
        ILogger<AttachmentEffect> logger)
    {
        _dispatcher = dispatcher;
        _composerStore = composerStore;
        _topicsStore = topicsStore;
        _spaceStore = spaceStore;
        _attachmentService = attachmentService;
        _uploader = uploader;
        _sessionService = sessionService;
        _topicService = topicService;
        _logger = logger;

        _attachRegistration = dispatcher.RegisterHandler<AttachFiles>(action =>
            HandleAttachAsync(action).LogFaults(_logger, nameof(AttachFiles)));
        _removeRegistration = dispatcher.RegisterHandler<RemoveAttachment>(CancelUpload);
    }

    public void Dispose()
    {
        _attachRegistration.Dispose();
        _removeRegistration.Dispose();
        foreach (var upload in _uploads.Values)
        {
            upload.Cancel();
            upload.Dispose();
        }
    }

    // Cancelling an upload in progress and removing a file that already finished are the same
    // action; the difference is only whether there is anything left to abort.
    private void CancelUpload(RemoveAttachment action)
    {
        if (_uploads.TryRemove(UploadKey(action.TopicId, action.LocalId), out var cts))
        {
            cts.Cancel();
            cts.Dispose();
        }
    }

    private async Task HandleAttachAsync(AttachFiles action)
    {
        var limits = await EnsureLimitsAsync();
        var topicId = await EnsureTopicAsync(action);
        if (topicId is null)
        {
            _dispatcher.Dispatch(new ShowError(NotLiveToast.Message));
            return;
        }

        var ticket = await _attachmentService.CreateUploadTicketAsync(topicId);
        if (!ticket.IsLive || ticket.Value is null)
        {
            _dispatcher.Dispatch(new ShowError(NotLiveToast.Message));
            return;
        }

        // Started together rather than one after another: waiting on the network is what the
        // person is being spared, and each file reports its own progress.
        await Task.WhenAll(action.Files.Select(file =>
            AttachOneAsync(topicId, ticket.Value.Token, file, limits)));
    }

    private async Task AttachOneAsync(
        string topicId, string ticket, PickedFile file, AttachmentLimits? limits)
    {
        var attachment = new ComposerAttachment
        {
            LocalId = Guid.NewGuid().ToString("N"),
            FileName = file.FileName,
            MediaType = file.MediaType,
            SizeBytes = file.SizeBytes
        };

        if (Refuse(file, limits, _composerStore.State.For(topicId).Count) is { } refusal)
        {
            _dispatcher.Dispatch(new AttachmentPicked(topicId, attachment));
            _dispatcher.Dispatch(new AttachmentFailed(topicId, attachment.LocalId, refusal));
            return;
        }

        _dispatcher.Dispatch(new AttachmentPicked(topicId, attachment));

        var cts = new CancellationTokenSource();
        _uploads[UploadKey(topicId, attachment.LocalId)] = cts;
        try
        {
            var outcome = await _uploader.UploadAsync(
                topicId,
                ticket,
                file,
                percent => _dispatcher.Dispatch(
                    new AttachmentProgressed(topicId, attachment.LocalId, percent)),
                cts.Token);

            _dispatcher.Dispatch<IAction>(outcome.Reference is not null
                ? new AttachmentUploaded(topicId, attachment.LocalId, outcome.Reference)
                : new AttachmentFailed(
                    topicId, attachment.LocalId, outcome.Error ?? $"{file.FileName} could not be uploaded."));
        }
        catch (OperationCanceledException)
        {
            // The person cancelled it, and the removal already took the file out of the composer.
            _logger.LogDebug("Upload of {FileName} was cancelled", file.FileName);
        }
        finally
        {
            if (_uploads.TryRemove(UploadKey(topicId, attachment.LocalId), out var finished))
            {
                finished.Dispose();
            }
        }
    }

    // The same rules the endpoint enforces, applied before a byte moves. The server refuses these
    // cases too; this is only about the person finding out immediately.
    private static string? Refuse(PickedFile file, AttachmentLimits? limits, int alreadyAttached)
    {
        if (limits is null)
        {
            return null;
        }

        if (file.SizeBytes > limits.MaxBytesPerFile)
        {
            return $"{file.FileName} is larger than the {limits.MaxBytesPerFile / (1024 * 1024)} MB limit.";
        }

        if (!limits.AllowedMediaTypes.Contains(file.MediaType, StringComparer.OrdinalIgnoreCase))
        {
            return $"{file.FileName} is not a kind this chat accepts; attach an image or a PDF.";
        }

        return alreadyAttached >= limits.MaxFilesPerMessage
            ? $"A message takes at most {limits.MaxFilesPerMessage} files."
            : null;
    }

    private async Task<AttachmentLimits?> EnsureLimitsAsync()
    {
        if (_composerStore.State.Limits is { } known)
        {
            return known;
        }

        var limits = await _attachmentService.GetLimitsAsync();
        if (limits is { IsLive: true, Value: not null })
        {
            _dispatcher.Dispatch(new AttachmentLimitsLoaded(limits.Value));
        }

        return limits.Value;
    }

    // A ticket is scoped to a topic, so there has to be one. Picking a file into an empty
    // composer is the person starting a conversation with a file rather than a sentence, so the
    // conversation is started here and named after the file.
    private async Task<string?> EnsureTopicAsync(AttachFiles action)
    {
        if (!string.IsNullOrEmpty(action.TopicId))
        {
            return await StartSessionIfNeededAsync(action.TopicId);
        }

        var state = _topicsStore.State;
        if (state.SelectedAgentId is null || action.Files.Count == 0)
        {
            return null;
        }

        var identity = ConversationIdGenerator.Create();
        var topic = new StoredTopic
        {
            TopicId = identity.TopicId,
            ChatId = identity.ChatId,
            ThreadId = identity.ThreadId,
            AgentId = state.SelectedAgentId,
            Name = action.Files[0].FileName,
            CreatedAt = DateTime.UtcNow,
            SpaceSlug = _spaceStore.State.CurrentSlug
        };

        var started = await _sessionService.StartSessionAsync(topic);
        if (!started.IsLive || !started.Value)
        {
            return null;
        }

        _dispatcher.Dispatch(new AddTopic(topic));
        _dispatcher.Dispatch(new SelectTopic(topic.TopicId));
        _dispatcher.Dispatch(new MessagesLoaded(topic.TopicId, []));
        await _topicService.SaveTopicAsync(topic.ToMetadata(), isNew: true);
        return topic.TopicId;
    }

    private async Task<string?> StartSessionIfNeededAsync(string topicId)
    {
        if (_sessionService.CurrentTopic?.TopicId == topicId)
        {
            return topicId;
        }

        var topic = _topicsStore.State.Topics.FirstOrDefault(t => t.TopicId == topicId);
        if (topic is null)
        {
            return null;
        }

        var started = await _sessionService.StartSessionAsync(topic);
        return started is { IsLive: true, Value: true } ? topicId : null;
    }

    private static string UploadKey(string topicId, string localId) => $"{topicId}/{localId}";
}