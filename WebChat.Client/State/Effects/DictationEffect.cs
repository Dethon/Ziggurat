using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using WebChat.Client.Contracts;
using WebChat.Client.Extensions;
using WebChat.Client.Models;
using WebChat.Client.Services;
using WebChat.Client.State.Composer;
using WebChat.Client.State.Space;
using WebChat.Client.State.Topics;

namespace WebChat.Client.State.Effects;

// The .NET half of a dictation. The browser owns the microphone, the encoder, the gesture and the
// upload; this hands it the two things only the live connection can produce — the address to post
// to and the permission to post — and turns what it reports back into composer state.
//
// Everything here is a decision. The clock, the level meter and the travel of the discard hint are
// the browser's, written straight to the DOM, because a per-frame trip through the WASM heap would
// buy nothing and cost the frame.
public sealed class DictationEffect : IDisposable
{
    private readonly Dispatcher _dispatcher;
    private readonly ComposerStore _composerStore;
    private readonly SpaceStore _spaceStore;
    private readonly IDictationService _dictationService;
    private readonly IAttachmentService _attachmentService;
    private readonly AttachmentEndpointResolver _endpoints;
    private readonly IDictationBridge _bridge;
    private readonly ILogger<DictationEffect> _logger;

    private readonly IDisposable _stopRegistration;
    private readonly IDisposable _discardRegistration;
    private readonly IDisposable _topicRegistration;

    private DotNetObjectReference<DictationEffect>? _self;

    public DictationEffect(
        Dispatcher dispatcher,
        ComposerStore composerStore,
        SpaceStore spaceStore,
        IDictationService dictationService,
        IAttachmentService attachmentService,
        AttachmentEndpointResolver endpoints,
        IDictationBridge bridge,
        ILogger<DictationEffect> logger)
    {
        _dispatcher = dispatcher;
        _composerStore = composerStore;
        _spaceStore = spaceStore;
        _dictationService = dictationService;
        _attachmentService = attachmentService;
        _endpoints = endpoints;
        _bridge = bridge;
        _logger = logger;

        _stopRegistration = dispatcher.RegisterHandler<StopDictation>(
            _ => _bridge.StopAsync().LogFaults(_logger, nameof(StopDictation)));
        _discardRegistration = dispatcher.RegisterHandler<DiscardDictation>(
            _ => _bridge.DiscardAsync().LogFaults(_logger, nameof(DiscardDictation)));

        // Words meant for one conversation must never surface in another, so leaving the topic
        // stops the microphone rather than letting the recording outlive the screen it started on.
        _topicRegistration = dispatcher.RegisterHandler<SelectTopic>(_ => DiscardIfRecording());
    }

    public async Task RegisterAsync(ElementReference microphone)
    {
        _self ??= DotNetObjectReference.Create(this);
        await _bridge.RegisterAsync(microphone, _self, await EnsureLimitsAsync());
    }

    // Minted per dictation, not per message: nothing is stored, so there is no slot to count and
    // no conversation to force into existence. The URL is resolved the same way the upload store's
    // is, because the transcription route lives beside it on the channel server.
    [JSInvokable]
    public async Task<DictationUpload?> MintTicketAsync()
    {
        var ticket = await _dictationService.CreateTicketAsync();
        if (ticket is not { IsLive: true, Value: not null })
        {
            return null;
        }

        var url = await _endpoints.ResolveAsync(Domain.DTOs.WebChat.DictationEndpointPaths.Transcriptions);
        var space = Uri.EscapeDataString(_spaceStore.State.CurrentSlug);
        return new DictationUpload(
            $"{url}?{Domain.DTOs.WebChat.DictationEndpointPaths.SpaceQueryParameter}={space}",
            ticket.Value.Token);
    }

    [JSInvokable]
    public void Started() => _dispatcher.Dispatch(new DictationStarted());

    [JSInvokable]
    public void Latched() => _dispatcher.Dispatch(new DictationLatched());

    [JSInvokable]
    public void Ended() => _dispatcher.Dispatch(new DictationEnded());

    [JSInvokable]
    public void Discarded() => _dispatcher.Dispatch(new DictationDiscarded());

    [JSInvokable]
    public void Transcribed(string text) => _dispatcher.Dispatch(new DictationTranscribed(text));

    [JSInvokable]
    public void Failed(string reason) => _dispatcher.Dispatch(new DictationFailed(reason));

    [JSInvokable]
    public void Unavailable(string reason) => _dispatcher.Dispatch(new DictationUnavailable(reason));

    [JSInvokable]
    public void MisTapped(string hint) => _dispatcher.Dispatch(new DictationMisTapped(hint));

    public void Dispose()
    {
        _stopRegistration.Dispose();
        _discardRegistration.Dispose();
        _topicRegistration.Dispose();
        _bridge.DisposeAsync().LogFaults(_logger, nameof(Dispose));
        _self?.Dispose();
    }

    private void DiscardIfRecording()
    {
        if (_composerStore.State.Dictation.IsRecording)
        {
            _dispatcher.Dispatch(new DiscardDictation());
        }
    }

    // The same call the composer already makes for the attachment rules, so the recording cap and
    // the mis-tap floor arrive with them and neither needs a client deploy to change.
    private async Task<DictationLimits> EnsureLimitsAsync()
    {
        var limits = _composerStore.State.Limits;
        if (limits is null)
        {
            var answered = await _attachmentService.GetLimitsAsync();
            if (answered is { IsLive: true, Value: not null })
            {
                _dispatcher.Dispatch(new AttachmentLimitsLoaded(answered.Value));
                limits = answered.Value;
            }
        }

        return limits is null
            ? new DictationLimits(120_000, 400)
            : new DictationLimits(limits.MaxDictationMs, limits.MinDictationMs);
    }
}