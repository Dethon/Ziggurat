using System.Collections.Concurrent;
using Domain.DTOs.WebChat;
using WebChat.Client.Contracts;
using WebChat.Client.Extensions;
using WebChat.Client.Models;
using WebChat.Client.State.Composer;
using WebChat.Client.State.Toast;

namespace WebChat.Client.State.Effects;

// Each file uploads as soon as it is picked, so typing carries on while the network does. A file
// that is too large or of a kind this chat does not take is refused here, at pick time, rather
// than after it has crossed the wire.
public sealed class AttachmentEffect : IDisposable
{
    private readonly Dispatcher _dispatcher;
    private readonly ComposerStore _composerStore;
    private readonly ComposerTopic _composerTopic;
    private readonly IAttachmentService _attachmentService;
    private readonly IAttachmentUploader _uploader;
    private readonly IChatLiveConnection _liveConnection;
    private readonly ILogger<AttachmentEffect> _logger;
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _uploads = new();

    // One ticket per message being composed, not per pick. The ticket is what counts a message's
    // files server-side, so minting a fresh one for every pick would let two picks of ten files
    // put twenty into one message with only the client's own check to stop it. It is dropped when
    // the composer empties, which is when the message it belonged to was sent.
    private readonly ConcurrentDictionary<string, UploadTicket> _tickets = new();

    private readonly IDisposable _attachRegistration;
    private readonly IDisposable _removeRegistration;
    private readonly IDisposable _clearRegistration;

    public AttachmentEffect(
        Dispatcher dispatcher,
        ComposerStore composerStore,
        ComposerTopic composerTopic,
        IAttachmentService attachmentService,
        IAttachmentUploader uploader,
        IChatLiveConnection liveConnection,
        ILogger<AttachmentEffect> logger)
    {
        _dispatcher = dispatcher;
        _composerStore = composerStore;
        _composerTopic = composerTopic;
        _attachmentService = attachmentService;
        _uploader = uploader;
        _liveConnection = liveConnection;
        _logger = logger;

        _attachRegistration = dispatcher.RegisterHandler<AttachFiles>(action =>
            HandleAttachAsync(action).LogFaults(_logger, nameof(AttachFiles)));
        _removeRegistration = dispatcher.RegisterHandler<RemoveAttachment>(CancelUpload);
        _clearRegistration = dispatcher.RegisterHandler<ClearAttachments>(
            action => _tickets.TryRemove(action.TopicId, out _));
    }

    public void Dispose()
    {
        _attachRegistration.Dispose();
        _removeRegistration.Dispose();
        _clearRegistration.Dispose();
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
        // A pick is the one user action that arrives from a page that was backgrounded while it
        // was being made: the picker holds the page down, and past the server's client timeout the
        // connection is gone by the time the file comes back. Everything below needs the hub — the
        // session, the limits, the ticket — so wait for the reconnect the resume already started
        // rather than refusing a file over a connection that is seconds from being back.
        if (!await _liveConnection.EnsureLiveAsync())
        {
            _dispatcher.Dispatch(new ShowError(NotLiveToast.Message));
            return;
        }

        var limits = await EnsureLimitsAsync();
        var topicId = await _composerTopic.EnsureAsync(action.TopicId, action.Files);
        if (topicId is null)
        {
            _dispatcher.Dispatch(new ShowError(NotLiveToast.Message));
            return;
        }

        var ticket = await EnsureTicketAsync(topicId);
        if (ticket is null)
        {
            _dispatcher.Dispatch(new ShowError(NotLiveToast.Message));
            return;
        }

        // Started together rather than one after another: waiting on the network is what the
        // person is being spared, and each file reports its own progress.
        await Task.WhenAll(action.Files.Select(file =>
            AttachOneAsync(topicId, ticket.Token, file, limits)));
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

        _dispatcher.Dispatch(new AttachmentPicked(topicId, attachment));

        if (Refuse(file, limits, topicId) is { } refusal)
        {
            _dispatcher.Dispatch(new AttachmentFailed(topicId, attachment.LocalId, refusal));
            return;
        }

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

    // The same rules the endpoint enforces, in the same words. The server refuses these cases
    // too; this is only about the person finding out immediately. A file already refused does not
    // count towards the maximum, because it is going nowhere — and this file is already in the
    // composer by now, so the count includes it.
    private string? Refuse(PickedFile file, AttachmentLimits? limits, string topicId)
    {
        if (limits is null)
        {
            return null;
        }

        var refusal = AttachmentRefusals.For(file.FileName, file.MediaType, file.SizeBytes, limits);
        if (refusal is not null)
        {
            return refusal;
        }

        var attached = ComposerSelectors.Sendable(_composerStore.State.For(topicId)).Count();
        return attached > limits.MaxFilesPerMessage
            ? AttachmentRefusals.TooManyFiles(limits.MaxFilesPerMessage)
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

    private async Task<UploadTicket?> EnsureTicketAsync(string topicId)
    {
        if (_tickets.TryGetValue(topicId, out var held) && held.ExpiresAt > DateTimeOffset.UtcNow)
        {
            return held;
        }

        var minted = await _attachmentService.CreateUploadTicketAsync(topicId);
        if (minted is not { IsLive: true, Value: not null })
        {
            return null;
        }

        _tickets[topicId] = minted.Value;
        return minted.Value;
    }

    private static string UploadKey(string topicId, string localId) => $"{topicId}/{localId}";
}