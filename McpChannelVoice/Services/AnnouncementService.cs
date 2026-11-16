using Domain.Contracts;
using Domain.DTOs.Metrics;
using Domain.DTOs.Metrics.Enums;
using Domain.DTOs.Voice;
using McpChannelVoice.Settings;

namespace McpChannelVoice.Services;

public sealed class AnnounceTargetNotFoundException(string message) : Exception(message);

public class AnnouncementService(
    SatelliteRegistry registry,
    SatelliteSessionRegistry sessions,
    ITextToSpeech tts,
    VoiceSettings settings,
    IMetricsPublisher metrics,
    ILogger<AnnouncementService> logger)
{
    public async Task<AnnounceResponse> AnnounceAsync(
        AnnounceRequest request,
        CancellationToken ct)
    {
        var targetIds = registry.Resolve(request.Target);
        if (targetIds.Count == 0)
        {
            // Log the requested target internally, but keep the client-facing message generic so the
            // 404 body doesn't disclose satellite ids / room names to callers.
            logger.LogWarning(
                "Announce target not found: id={Id} room={Room} all={All}",
                request.Target.SatelliteId, request.Target.Room, request.Target.All);
            throw new AnnounceTargetNotFoundException("No matching satellites for the requested target.");
        }

        var announcementId = Guid.NewGuid().ToString("N");
        var outcomes = new List<AnnouncementOutcome>();

        foreach (var id in targetIds)
        {
            var session = sessions.Get(id);
            if (session is null)
            {
                // No live session, but the registry still knows the satellite's room/identity, so the
                // offline error carries the same context fields as the online announce metrics —
                // through the same stamp, which is what keeps all three of them present.
                outcomes.Add(new AnnouncementOutcome { Id = id, Status = AnnouncementOutcome.Offline });
                metrics.Publish(new VoiceEvent
                {
                    Metric = VoiceMetric.AnnounceError,
                    Priority = request.Priority.ToString(),
                    Outcome = "offline"
                }.About(SatelliteIdentity.Of(id, registry.GetById(id))));
                continue;
            }

            // An explicitly requested voice still wins: the caller asked for this announcement to be
            // read in it, which outranks the satellite's standing preference.
            var options = new SynthesisOptions { Voice = request.Voice ?? session.ResolveVoice(settings) };

            var job = new PlaybackJob(
                Label: $"announce:{announcementId}",
                Kind: PlaybackKind.Announce,
                Priority: request.Priority,
                Audio: tts.SynthesizeAsync(request.Text, options, ct),
                // Published at first audio rather than when the queue reaches the job: "played" then
                // means audio actually reached the satellite, so an announcement whose synthesis
                // fails is no longer counted as played with nothing else ever recorded for it.
                OnFirstAudio: _ =>
                {
                    metrics.Publish(AnnounceEvent(VoiceMetric.AnnouncePlayed, session, request));
                    return Task.CompletedTask;
                });

            var ticket = session.Playback.Enqueue(job);
            // A satellite that went away between the session lookup and the enqueue is offline, not
            // busy — which is the truthful status the response already had a code path for. The
            // other two reasons are a queue that had no room, which is a drop.
            var status = ticket.Refused switch
            {
                null => "queued",
                RefusalReason.QueueClosed => "offline",
                _ => "dropped"
            };
            outcomes.Add(new AnnouncementOutcome { Id = id, Status = status });

            metrics.Publish(AnnounceEvent(
                ticket.Refused is null ? VoiceMetric.AnnounceQueued : VoiceMetric.AnnounceError,
                session, request) with
            { Outcome = status });

            // Runs unobserved on the queue's signal, so it guards itself.
            _ = ticket.Completed.ContinueWith(
                settled =>
                {
                    try
                    {
                        if (settled.Result.Kind == PlaybackOutcomeKind.Preempted)
                        {
                            metrics.Publish(AnnounceEvent(
                                VoiceMetric.AnnouncePreemptedReply, session, request));
                        }
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex, "Announce outcome reaction failed for {Id}", id);
                    }
                },
                TaskScheduler.Default);
        }

        logger.LogInformation("Announce {Id} -> {N} targets ({Status})",
            announcementId, outcomes.Count,
            string.Join(",", outcomes.Select(o => $"{o.Id}={o.Status}")));

        return new AnnounceResponse { AnnouncementId = announcementId, Satellites = outcomes };
    }

    // Every announce metric carries the same room and identity context, offline targets included.
    // The id the loop is on and the session's own are the same value — the registry keys sessions by
    // SatelliteId — so stamping from the session leaves the payload as it was.
    private static VoiceEvent AnnounceEvent(
        VoiceMetric metric, SatelliteSession session, AnnounceRequest request) => new VoiceEvent
        {
            Metric = metric,
            Priority = request.Priority.ToString()
        }.About(session);
}