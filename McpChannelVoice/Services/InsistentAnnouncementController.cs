using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Domain.Contracts;
using Domain.DTOs.Metrics;
using Domain.DTOs.Metrics.Enums;
using Domain.DTOs.Voice;
using McpChannelVoice.Services.WyomingProtocol;
using McpChannelVoice.Settings;

namespace McpChannelVoice.Services;

// Drives an insistent alert: plays the message (High priority) on every targeted online satellite and
// repeats on a gap until acknowledged (out-of-band, via ActiveAlertRegistry) or a safety cap. The
// satellite mics only on local wake, so there is no mic window here — acknowledgment arrives when the
// user wakes a satellite and WyomingSatelliteHost calls ActiveAlertRegistry.Acknowledge.
public sealed class InsistentAnnouncementController(
    SatelliteRegistry registry,
    SatelliteSessionRegistry sessions,
    ITextToSpeech tts,
    VoiceSettings settings,
    ActiveAlertRegistry alerts,
    IMetricsPublisher metrics,
    TimeProvider time,
    IHttpClientFactory httpClientFactory,
    ILogger<InsistentAnnouncementController> logger) : IInsistentAnnouncer
{
    public async Task<AnnounceResponse> StartAsync(AnnounceRequest request, CancellationToken ct)
    {
        var targetIds = registry.Resolve(request.Target);
        if (targetIds.Count == 0)
        {
            logger.LogWarning(
                "Insistent announce target not found: id={Id} room={Room} all={All}",
                request.Target.SatelliteId, request.Target.Room, request.Target.All);
            throw new AnnounceTargetNotFoundException("No matching satellites for the requested target.");
        }

        var announcementId = Guid.NewGuid().ToString("N");

        var offlineIds = targetIds.Where(id => sessions.Get(id) is null).ToList();
        offlineIds.ForEach(id => metrics.Publish(new VoiceEvent
        {
            Metric = VoiceMetric.AlarmOffline,
            Outcome = "offline"
        }.About(SatelliteIdentity.Of(id, registry.GetById(id)))));

        if (offlineIds.Count == targetIds.Count)
        {
            // The alarm never rang — exactly when the phone must find out. rounds=0 marks
            // "never spoken"; fire-and-forget so the caller's HTTP response isn't held by the POST.
            _ = Task.Run(() => TryEscalateAsync(request, targetIds, 0));
            return new AnnounceResponse
            {
                AnnouncementId = announcementId,
                Satellites = targetIds.Select(id => new AnnouncementOutcome { Id = id, Status = AnnouncementOutcome.Offline }).ToList()
            };
        }

        var plan = InsistentPlan.Resolve(request.Insistent, settings.Announce.Insistent);
        var handle = new AlertHandle(new CancellationTokenSource(), targetIds, request.Text, request.Kind);
        alerts.Register(handle);

        _ = Task.Run(() => RunLoopAsync(announcementId, request, plan, handle, targetIds));

        return new AnnounceResponse
        {
            AnnouncementId = announcementId,
            Satellites = targetIds.Select(id =>
                new AnnouncementOutcome { Id = id, Status = sessions.Get(id) is not null ? AnnouncementOutcome.Started : AnnouncementOutcome.Offline }).ToList()
        };
    }

    private async Task RunLoopAsync(
        string announcementId, AnnounceRequest request, InsistentPlan plan, AlertHandle handle, IReadOnlyList<string> targetIds)
    {
        try
        {
            var buffered = await BufferAudioAsync(request, handle.Token);
            var start = time.GetTimestamp();
            var round = 0;

            while (!handle.Token.IsCancellationRequested
                   && round < plan.MaxRepeats
                   && (plan.MaxDuration is not { } max || time.GetElapsedTime(start) < max))
            {
                var gain = plan.GainFor(round);

                // Re-asserted every round, not once before the loop: a satellite that reconnects
                // mid-alarm comes back with no hold, and one that was rebooting when the loop
                // started never got the first one. Both heal within a round. The satellite's hold
                // is idempotent, so a re-assert to a satellite that already holds costs nothing.
                await SendSpeakerVolumeAsync(targetIds, "alert-hold");

                foreach (var session in OnlineSessions(targetIds))
                {
                    session.Playback.Enqueue(BuildJob(announcementId, buffered, session, gain));
                }
                round++;

                // Skip the gap delay when this was the last round (cap reached or token already
                // cancelled). Doing the delay would require the test to advance fake time once more,
                // and it would defer the AlarmUnacknowledged/AlarmAcknowledged publish unnecessarily.
                var capReached = round >= plan.MaxRepeats
                    || handle.Token.IsCancellationRequested
                    || (plan.MaxDuration is { } maxLeft && time.GetElapsedTime(start) >= maxLeft);
                if (capReached)
                {
                    break;
                }

                try
                {
                    await Task.Delay(plan.Gap, time, handle.Token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }

            if (handle.IsAcknowledged)
            {
                foreach (var session in OnlineSessions(targetIds))
                {
                    session.Playback.PreemptCurrent();
                }
                metrics.Publish(AlarmEvent(VoiceMetric.AlarmAcknowledged, targetIds, round));
            }
            else
            {
                metrics.Publish(AlarmEvent(VoiceMetric.AlarmUnacknowledged, targetIds, round));

                // The alert is finished — take it out of the registry before the potentially slow
                // escalation webhook so a wake during the POST can't dismiss a dead alarm into snooze
                // context (Discard is idempotent, so the finally's safety-net call is a no-op below).
                alerts.Discard(handle);
                await TryEscalateAsync(request, targetIds, round);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Insistent alert {Id} loop failed", announcementId);
        }
        finally
        {
            // Discard BEFORE asking who is still covered, so this alert no longer counts itself.
            // Discard is idempotent, so the unacknowledged branch's earlier call is harmless.
            alerts.Discard(handle);
            await SendSpeakerVolumeAsync(
                targetIds.Where(id => alerts.CountFor(id) == 0), "alert-release");
        }
    }

    // A local mute is the user's, but it must not silence a timer or an alarm. The hold unmutes
    // the speaker for the duration of the ring; the release restores whatever the user had set.
    // Both are best-effort: a satellite that never got the hold simply rings at its current level.
    private async Task SendSpeakerVolumeAsync(IEnumerable<string> targetIds, string action)
    {
        var evt = WyomingEvent.Header("speaker-volume", new JsonObject { ["action"] = action });
        foreach (var session in OnlineSessions(targetIds))
        {
            await session.TrySendControlAsync(evt, CancellationToken.None);
        }
    }

    private async Task<IReadOnlyList<AudioChunk>> BufferAudioAsync(AnnounceRequest request, CancellationToken ct)
    {
        // One synthesis per alert, replayed every round/satellite. Per-satellite voice overrides are not
        // applied to insistent alerts in v1 (single synthesis); the request voice or global voice is used.
        var voice = request.Voice ?? settings.Tts.OpenAi.Voice;
        var options = new SynthesisOptions { Voice = voice };
        var chunks = new List<AudioChunk> { AlarmTone.Chunk(request.Kind) };
        await foreach (var chunk in tts.SynthesizeAsync(request.Text, options, ct))
        {
            chunks.Add(chunk);
        }
        return chunks;
    }

    private PlaybackJob BuildJob(
        string announcementId, IReadOnlyList<AudioChunk> buffered, SatelliteSession session, double gain) =>
        new(
            Label: $"alarm:{announcementId}",
            // The only place the alarm kind is minted. This controller handles exactly the insistent
            // announces — timers and alarms — so the satellite's non-attenuated alert route is
            // reached by those and nothing else.
            Kind: PlaybackKind.Alarm,
            Priority: AnnouncePriority.High,
            Audio: Replay(buffered, gain),
            // At first audio, so a round counts as played only once the alert actually reached the
            // speaker rather than when the queue reached the job.
            OnFirstAudio: _ =>
            {
                metrics.Publish(new VoiceEvent
                {
                    Metric = VoiceMetric.AnnouncePlayed,
                    Priority = AnnouncePriority.High.ToString()
                }.About(session));
                return Task.CompletedTask;
            });

    private IEnumerable<SatelliteSession> OnlineSessions(IEnumerable<string> targetIds) =>
        targetIds.Select(sessions.Get).Where(s => s is not null).Select(s => s!);

    // Reported about the first target: an alert covering several satellites is one ring, and the
    // metric names where it was aimed. Stamped through the same identity as every other report, so
    // the room and the household cannot go missing from it.
    private VoiceEvent AlarmEvent(VoiceMetric metric, IReadOnlyList<string> targetIds, int rounds)
    {
        var evt = new VoiceEvent { Metric = metric, DurationMs = rounds };
        return targetIds.Count == 0
            ? evt
            : evt.About(SatelliteIdentity.Of(targetIds[0], registry.GetById(targetIds[0])));
    }

    private static async IAsyncEnumerable<AudioChunk> Replay(IReadOnlyList<AudioChunk> chunks, double gain)
    {
        foreach (var chunk in chunks)
        {
            yield return chunk with { Data = PcmGain.Apply(chunk.Data, gain) };
        }
        await Task.CompletedTask;
    }

    // Ack-gated escalation: an unacknowledged ALARM (never a timer) is handed to HA via webhook so an
    // automation can notify another channel. Fire-and-forget: failures are logged, never retried.
    private async Task TryEscalateAsync(AnnounceRequest request, IReadOnlyList<string> targetIds, int rounds)
    {
        var url = settings.Announce.Escalation.WebhookUrl;
        if (request.Kind != AnnounceKind.Alarm || string.IsNullOrWhiteSpace(url))
        {
            return;
        }

        try
        {
            var client = httpClientFactory.CreateClient(nameof(InsistentAnnouncementController));
            using var response = await client.PostAsJsonAsync(
                url, new { text = request.Text, satellites = targetIds, rounds });
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("Alarm escalation webhook returned {Status}", response.StatusCode);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Alarm escalation webhook failed");
        }
    }
}