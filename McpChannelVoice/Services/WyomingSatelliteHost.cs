using System.Diagnostics;
using System.Text.Json.Nodes;
using Domain.Contracts;
using Domain.DTOs.Metrics;
using Domain.DTOs.Metrics.Enums;
using Domain.DTOs.Voice;
using McpChannelVoice.Services.Stt;
using McpChannelVoice.Services.Verification;
using McpChannelVoice.Services.WyomingProtocol;
using McpChannelVoice.Settings;

namespace McpChannelVoice.Services;

// Dials each configured satellite as a Wyoming client. The satellite is itself
// a Wyoming server: it runs local wake detection and, once the wake word fires,
// sends us run-pipeline followed by an open-ended mic audio stream. We segment
// that stream with SilenceGate, transcribe it, and send a transcript back to stop
// streaming and re-arm the satellite. TTS replies flow the other way as
// audio-start/audio-chunk/audio-stop frames driven by the session playback loop.
public sealed class WyomingSatelliteHost(
    WyomingClientSettings settings,
    VoiceSettings voiceSettings,
    SatelliteRegistry satelliteRegistry,
    SatelliteSessionRegistry sessionRegistry,
    VoiceConversationManager conversationManager,
    ISpeechToText speechToText,
    TranscriptDispatcher dispatcher,
    ActiveAlertRegistry alerts,
    IMetricsPublisher metrics,
    TimeProvider time,
    WakeArbiter arbiter,
    SilenceGateFactory gates,
    ILogger<WyomingSatelliteHost> logger,
    ISpeakerVerifier? speakerVerifier = null) : IHostedService
{
    private CancellationTokenSource? _cts;
    private readonly List<Task> _connections = [];

    public Task StartAsync(CancellationToken ct)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var token = _cts.Token;

        var dialable = satelliteRegistry.GetAllIds()
            .Select(id => (Id: id, Config: satelliteRegistry.GetById(id)!))
            .Where(s => !string.IsNullOrWhiteSpace(s.Config.Address))
            .ToList();

        if (dialable.Count == 0)
        {
            logger.LogWarning(
                "No satellites with an Address configured ({Total} known) — the hub will not dial any satellite. " +
                "Set Satellites:<id>:Address (env Satellites__<id>__Address, e.g. tcp://host.docker.internal:10800).",
                satelliteRegistry.GetAllIds().Count);
        }
        else
        {
            logger.LogInformation("Dialing {Count} satellite(s): {Ids}",
                dialable.Count, string.Join(", ", dialable.Select(s => s.Id)));
        }

        foreach (var (id, config) in dialable)
        {
            if (!TryParseAddress(config.Address!, out var host, out var port))
            {
                logger.LogError("Satellite {Id} has invalid address '{Address}', skipping", id, config.Address);
                continue;
            }
            _connections.Add(Task.Run(() => ConnectionLoopAsync(id, config, host, port, token), token));
        }

        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken ct)
    {
        if (_cts is null)
        {
            return;
        }
        await _cts.CancelAsync();
        try
        {
            await Task.WhenAll(_connections);
        }
        catch
        {
            // Connection loops unwind on cancellation; surfaced faults are expected here.
        }
        finally
        {
            _cts.Dispose();
        }
    }

    private async Task ConnectionLoopAsync(string id, SatelliteConfig config, string host, int port, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await RunConnectionAsync(id, config, host, port, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Satellite {Id} connection to {Host}:{Port} dropped", id, host, port);
            }

            if (ct.IsCancellationRequested)
            {
                break;
            }
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(settings.ReconnectDelaySeconds), ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task RunConnectionAsync(string id, SatelliteConfig config, string host, int port, CancellationToken ct)
    {
        // The client stays in this scope so dialling and disposal stay together; the connection
        // borrows only the two halves of the wire it needs.
        await using var client = new WyomingClient();
        await client.ConnectAsync(host, port, ct);
        logger.LogInformation("Connected to satellite {Id} at {Host}:{Port}", id, host, port);

        var connection = CreateConnection(id, config, client.WriteAsync);
        await connection.RunAsync(client.ReadAllAsync(ct), ct);
    }

    // A fully wired connection for one satellite, with the real transcription, verification and
    // telemetry helpers bound. Internal because a test reaches it directly: driving Wyoming events
    // into a connection the host assembled is what exercises the real publishing code rather than
    // stand-ins for it.
    internal SatelliteConnection CreateConnection(
        string id, SatelliteConfig config, Func<WyomingEvent, CancellationToken, Task> writer)
    {
        var session = new SatelliteSession(id, config, new PlaybackQueue(
            replyMaxDepth: voiceSettings.Tts.Streaming.MaxQueuedSegments,
            announceMaxDepth: voiceSettings.Announce.QueueMaxDepth,
            // No prefetch size means no prefetch: a segment's synthesis then starts when the loop
            // reaches it, which is the kill switch the setting has always been.
            prefetchBufferChunks: voiceSettings.Tts.Streaming.Prefetch
                ? voiceSettings.Tts.Streaming.PrefetchBufferChunks
                : null),
            // The one gate factory, so every capture on this satellite — wake turn, follow-up and
            // approval answer — pays back into the same per-satellite room-noise memory.
            new Microphone(id, gates));
        var followUp = voiceSettings.FollowUp with
        {
            Enabled = config.FollowUpEnabled ?? voiceSettings.FollowUp.Enabled
        };

        return new SatelliteConnection(sessionRegistry, arbiter, alerts, time, logger)
        {
            Session = session,
            Coordinator = BuildCoordinator(session, writer, followUp),
            Writer = writer,
            OnPlaybackError = ex =>
            {
                metrics.Publish(new VoiceEvent
                {
                    Metric = VoiceMetric.TtsError,
                    Error = ex.Message,
                    ConversationId = conversationManager.GetActiveConversationId(id)
                }.About(session));
                return Task.CompletedTask;
            }
        };
    }

    private FollowUpConversation BuildCoordinator(
        SatelliteSession session, Func<WyomingEvent, CancellationToken, Task> writer, FollowUpSettings followUp)
    {
        var capture = new CaptureSession(
            session, gates, time, voiceSettings.Arbitration.HistorySpan,
            // Reached only by a wake turn, carrying what the satellite reported about the wake that
            // opened it. A follow-up has no counterpart, so it cannot report a loudness it never
            // measured.
            onWakeTurn: announcement => PublishVoiceMetric(
                VoiceMetric.WakeTriggered, session,
                wakeRms: announcement?.Rms, wakeScore: announcement?.Score));

        return new FollowUpConversation(followUp, time)
        {
            Capture = capture,
            Turn = session.Turn,
            TranscribeAndDispatch = (utterance, isFollowUp, token) =>
                TranscribeAndDispatchAsync(session, utterance, isFollowUp, token),
            EnqueueChime = token => EnqueueChimeAsync(session, token),
            EndConversation = token => writer(
                WyomingEvent.Header("transcript", new JsonObject { ["text"] = string.Empty }), token),
            OnFollowUpWindow = token =>
            {
                PublishVoiceMetric(VoiceMetric.FollowUpWindowOpened, session);
                return Task.CompletedTask;
            },
            OnSilenceTimeout = (stats, token) =>
            {
                PublishVoiceMetric(VoiceMetric.FollowUpTimedOut, session, stats);
                return Task.CompletedTask;
            },
            OnReplyTimeout = token =>
            {
                logger.LogWarning(
                    "Reply handshake timed out for {Id} after {TimeoutMs}ms; ending conversation and re-arming wake",
                    session.SatelliteId, followUp.ReplyTimeoutMs);
                return Task.CompletedTask;
            },
            EarlyVerifyMs = speakerVerifier is null ? 0 : voiceSettings.SpeakerVerification.EarlyVerifyMs,
            EarlyReject = (utterance, token) => EarlyRejectAsync(session, utterance, token)
        };
    }

    // Early-close speaker check: verify the audio captured so far and, if it is an unknown voice
    // (e.g. background TV that latched as speech), reject it now so the loop closes the capture
    // instead of holding the mic open to trailing silence / the max-utterance cap. Sub-speech and
    // fail-open cases return false (keep capturing), so enrolled voices are never truncated.
    private async Task<bool> EarlyRejectAsync(
        SatelliteSession session, UtteranceCapture capture, CancellationToken ct)
    {
        if (speakerVerifier is null)
        {
            return false;
        }

        var stats = capture.Stats;
        // A capture still open at the early mark is not necessarily someone speaking — a
        // follow-up window holds the mic open regardless of whether anyone has said anything, so
        // a capture can sit here with zero gate-classified speech (pure room noise). Keep the
        // short-utterance skip: with nothing to embed yet, VerifyAsync returns Skipped rather than
        // judging silence as an unknown voice, so the capture keeps running instead of being
        // rejected on a foregone conclusion. Once real speech (TV or otherwise) has latched, it
        // clears MinVerifySpeechMs on its own and this check still applies to it as before.
        var sw = Stopwatch.StartNew();
        var verification = await speakerVerifier.VerifyAsync(
            capture.BufferedAudio, stats.SpeechMs, session.Config, ct, enforceMinSpeech: true);
        sw.Stop();
        PublishVerifyLatency(
            VoiceMetric.SpeakerVerifyEarlyMs, session, sw.ElapsedMilliseconds, verification.Similarity, "early");
        if (verification.Decision != SpeakerDecision.Rejected)
        {
            return false;
        }

        logger.LogInformation(
            "Early-rejecting capture from {Id}: unknown speaker (similarity {Similarity:F3})",
            session.SatelliteId, verification.Similarity);
        PublishUnknownSpeaker(session, stats, verification.Similarity, "unknown_speaker_early");
        return true;
    }

    // Verification runs before the STT stopwatch starts, so without this the ONNX embedding is pure
    // invisible latency. The two passes report under
    // DIFFERENT metrics — the final inline pass (SpeakerVerifyMs) is additive within the turn
    // decomposition, while the early pass (SpeakerVerifyEarlyMs) runs concurrently with the user
    // still speaking and overlaps the utterance, so averaging them together is meaningless. Outcome
    // still carries "early"/"final" for readers that want it in one place.
    private void PublishVerifyLatency(
        VoiceMetric metric, SatelliteSession session, long elapsedMs, double? similarity, string outcome) =>
        metrics.Publish(new VoiceEvent
        {
            Metric = metric,
            Outcome = outcome,
            DurationMs = elapsedMs,
            Similarity = similarity,
            ConversationId = conversationManager.GetActiveConversationId(session.SatelliteId)
        }.About(session));

    // Rejection telemetry is diagnostic, not part of the turn contract.
    private void PublishUnknownSpeaker(
        SatelliteSession session, CaptureStats stats, double? similarity, string outcome) =>
        metrics.Publish(new VoiceEvent
        {
            Metric = VoiceMetric.UtteranceRejected,
            Outcome = outcome,
            Similarity = similarity,
            PeakRms = stats.PeakRms,
            SpeechMs = stats.SpeechMs,
            FloorRms = stats.FloorRms,
            TrailingRms = stats.TrailingRms,
            EndReason = stats.EndReason,
            ConversationId = conversationManager.GetActiveConversationId(session.SatelliteId)
        }.About(session));

    // Returns true only when the transcript actually reached the agent. Empty/low-confidence
    // transcripts and STT errors return false so the conversation ends and wake re-arms, rather
    // than the loop blocking forever on a reply handshake the agent will never complete.
    private async Task<bool> TranscribeAndDispatchAsync(
        SatelliteSession session, UtteranceCapture capture, bool isFollowUp, CancellationToken ct)
    {
        try
        {
            // The endpointing tail is audio-domain time (derived from PCM frame durations), so it is
            // exact and immune to scheduling jitter. Published unconditionally — including on the
            // paths that go on to drop the transcript — because tuning TrailingSilenceMs needs the
            // rejected captures too.
            metrics.Publish(new VoiceEvent
            {
                Metric = VoiceMetric.EndpointTailMs,
                DurationMs = capture.Stats.TrailingSilenceMs,
                EndReason = capture.Stats.EndReason,
                ConversationId = conversationManager.GetActiveConversationId(session.SatelliteId)
            }.About(session));

            double? similarity = null;
            string? identifiedSpeaker = null;
            SpeakerVerification? verification = null;
            if (speakerVerifier is not null)
            {
                // Follow-up captures skip the short-utterance protection: a follow-up window
                // reopens the mic wake-free beside a talking TV, so a short TV burst must be
                // verified and rejected rather than passed through. First-turn captures keep the
                // skip so a genuinely brief opening command stays safe.
                var verifySw = Stopwatch.StartNew();
                verification = await speakerVerifier.VerifyAsync(
                    capture.BufferedAudio, capture.Stats.SpeechMs, session.Config, ct,
                    enforceMinSpeech: !isFollowUp);
                verifySw.Stop();
                PublishVerifyLatency(
                    VoiceMetric.SpeakerVerifyMs, session, verifySw.ElapsedMilliseconds,
                    verification.Value.Similarity, "final");
                if (verification.Value.Decision == SpeakerDecision.Rejected)
                {
                    logger.LogInformation(
                        "Rejecting capture from {Id}: unknown speaker (similarity {Similarity:F3})",
                        session.SatelliteId, verification.Value.Similarity);
                    PublishUnknownSpeaker(
                        session, capture.Stats, verification.Value.Similarity, "unknown_speaker");
                    return false;
                }
                similarity = verification.Value.Similarity;
                // A conclusive match names the speaker's profile, which the dispatcher resolves to
                // their account; the doubtful band leaves this null, and the turn is the guest's.
                identifiedSpeaker = verification.Value.IdentifiedSpeaker;
                if (similarity is not null)
                {
                    // Accepted scores only reach Redis telemetry; log them too so threshold
                    // calibration doesn't require correlating metrics with transcripts offline.
                    logger.LogInformation(
                        "Accepting capture from {Id}: similarity {Similarity:F3}, speaker {Speaker}",
                        session.SatelliteId, similarity, identifiedSpeaker ?? session.Config.Identity);
                }
            }

            var sw = Stopwatch.StartNew();
            // Honor a per-satellite STT language override (symmetric with the per-satellite
            // Tts.OpenAi.Voice override resolved in SendReplyTool/AnnouncementService); null falls
            // back to the global Stt.OpenAi.Language inside the backend. The factory also derives
            // the TSE hints (target speaker, noise floor) from the speaker gate's verdict.
            var options = TranscriptionOptionsFactory.Create(
                session.SatelliteId, session.Config, verification, capture.Stats);
            var result = await speechToText.TranscribeAsync(capture.Audio, options, ct);
            sw.Stop();

            metrics.Publish(new VoiceEvent
            {
                Metric = VoiceMetric.SttLatencyMs,
                DurationMs = sw.ElapsedMilliseconds,
                ConversationId = conversationManager.GetActiveConversationId(session.SatelliteId)
            }.About(session));

            if (isFollowUp)
            {
                PublishVoiceMetric(VoiceMetric.FollowUpEngaged, session);
            }

            // Taken BEFORE the dispatch: DispatchAsync does real work inside — GetOrCreateAsync is a
            // full create_conversation MCP round trip on a conversation's first turn (the normal case
            // for a one-shot command, given the 5-minute mapping expiry), plus the channel/message
            // write and an awaited Redis publish. Stamped afterwards, all of that sat in no span at
            // all; stamped here it lands inside AgentRoundTripMs, where it belongs.
            var dispatchStartedAt = time.GetTimestamp();
            var dispatched = await dispatcher.DispatchAsync(
                session, result, voiceSettings.AgentId, capture.Stats, similarity, identifiedSpeaker, ct);
            if (dispatched)
            {
                session.Turn.MarkDispatched(dispatchStartedAt);
                // Wake (above) is the primary dismissal path; this is a harmless fallback for turns
                // where a wake event was not observed. The registry makes a second Acknowledge a no-op.
                // Runs AFTER this dispatch, so its snooze context lands on the NEXT transcript.
                session.NoteDismissals(alerts.Acknowledge(session.SatelliteId), time.GetUtcNow());
            }
            return dispatched;
        }
        catch (OperationCanceledException)
        {
            // Connection tearing down.
            return false;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Transcription failed for {Id}", session.SatelliteId);
            metrics.Publish(new VoiceEvent
            {
                Metric = VoiceMetric.SttError,
                Error = ex.Message,
                ConversationId = conversationManager.GetActiveConversationId(session.SatelliteId)
            }.About(session));
            return false;
        }
    }

    private async Task EnqueueChimeAsync(SatelliteSession session, CancellationToken ct)
    {
        var job = new PlaybackJob(
            Label: $"chime:{session.SatelliteId}",
            Kind: PlaybackKind.Chime,
            Priority: AnnouncePriority.High,
            Audio: ListeningChime.Stream());

        // The token is this caller's own reason to stop waiting — its connection tearing down —
        // not a guard against hanging: the queue settles every job it is handed, teardown included.
        await session.Playback.Enqueue(job).Completed.WaitAsync(ct);
    }

    private void PublishVoiceMetric(
        VoiceMetric metric, SatelliteSession session, CaptureStats? stats = null,
        double? wakeRms = null, double? wakeScore = null) =>
        metrics.Publish(new VoiceEvent
        {
            Metric = metric,
            PeakRms = stats?.PeakRms,
            SpeechMs = stats?.SpeechMs,
            FloorRms = stats?.FloorRms,
            TrailingRms = stats?.TrailingRms,
            EndReason = stats?.EndReason,
            WakeRms = wakeRms,
            WakeScore = wakeScore,
            ConversationId = conversationManager.GetActiveConversationId(session.SatelliteId)
        }.About(session));

    private static bool TryParseAddress(string address, out string host, out int port)
    {
        host = string.Empty;
        port = 0;
        if (!Uri.TryCreate(address, UriKind.Absolute, out var uri) || uri.Port <= 0 || string.IsNullOrEmpty(uri.Host))
        {
            return false;
        }
        host = uri.Host;
        port = uri.Port;
        return true;
    }
}