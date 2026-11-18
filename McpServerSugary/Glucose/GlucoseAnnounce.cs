using System.Text.Json;
using Domain.Contracts;
using Domain.DTOs.Channel;
using Domain.DTOs.Voice;
using Domain.Exceptions;
using Domain.Tools.Sugary.Watches;
using Microsoft.Extensions.Logging;

namespace McpServerSugary.Glucose;

// A glucose watch's alarm: rung on the satellites its target names, through the subject's household's
// own voice server, as a timer is; and where nothing there would be heard — no target, every satellite
// it names offline, the voice server unreachable — on the channels of the person whose sensor it is and
// of whoever wrote the watch, through the agent. Its text is said as written: a fixed sentence, as a
// home watch's announcement is, so the detail is a prompt effect's to tell.
public sealed class GlucoseAnnounce(
    IHouseholdVoices voices,
    IChannelAlerts alerts,
    ILogger<GlucoseAnnounce> logger) : IGlucoseAnnounceEffect
{
    // A timer's ring: heard at once at full volume, ten seconds apart for two minutes, which is also
    // how long a channel alert rings (AlertRelay.RingWindow). An alarm with no insistence would be a
    // one-shot the hub keeps nothing of, so nothing could stop it and nobody would be asked to.
    private static readonly InsistentOptions _timerRing = new()
    {
        GapSeconds = 10, MaxRepeats = 12, RampStartPercent = 100
    };

    private static readonly JsonSerializerOptions _json = new() { PropertyNameCaseInsensitive = true };

    public async Task<bool> RingAsync(
        FiredGlucoseWatch watch, GlucoseAnnounceEffect effect, GlucoseTriggerFire report, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(watch);
        ArgumentNullException.ThrowIfNull(effect);
        try
        {
            return await RingOnSatellitesAsync(watch, effect, ct) || await RingOnChannelsAsync(watch, effect, ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            // The hub answering wrongly — a satellite gone since the watch was written — or the channels
            // failing rang nothing, which the engine is told so a `once` watch stays armed.
            logger.LogWarning(ex, "Glucose watch {WatchId}'s alarm failed to ring", watch.Watch.Id);
            return false;
        }
    }

    // False when nothing there would hear it, and it is the channels' to ring.
    private async Task<bool> RingOnSatellitesAsync(FiredGlucoseWatch watch, GlucoseAnnounceEffect effect, CancellationToken ct)
    {
        if (effect.Target?.Deserialize<AnnounceTarget>(_json) is not { } target)
        {
            return false;
        }

        try
        {
            var rung = await voices.For(watch.Subject.TenantId).Announcer.StartAsync(new AnnounceRequest
            {
                Target = target,
                Text = effect.Text,
                Kind = AnnounceKind.Alarm,
                Insistent = Insistence(watch, effect)
            }, ct);
            if (RangNowhere(rung))
            {
                logger.LogInformation(
                    "Glucose watch {WatchId}'s satellites are all offline; ringing it on its people's channels", watch.Watch.Id);
                return false;
            }

            logger.LogInformation("Glucose watch {WatchId} rang on its household's satellites", watch.Watch.Id);
            return true;
        }
        catch (VoiceHubUnavailableException ex) when (!ct.IsCancellationRequested)
        {
            // The household's voice server is not there to take it (its kit restarting, its house
            // without power), so no satellite would be heard: it rings where one with no target rings.
            logger.LogWarning("Glucose watch {WatchId} could not reach its voice server ({Reason}); ringing it on its people's channels",
                watch.Watch.Id, ex.Message);
            return false;
        }
    }

    private static bool RangNowhere(AnnounceResponse rung) =>
        rung.Satellites.Count > 0 && rung.Satellites.All(s => s.Status == AnnouncementOutcome.Offline);

    // Hers when written, each field left out the voice server's alarm default as a home watch's reads;
    // left out whole, a timer's ring. One the hub could not read rings as a timer rather than not at all.
    private InsistentOptions Insistence(FiredGlucoseWatch watch, GlucoseAnnounceEffect effect)
    {
        if (effect.Insistent is null)
        {
            return _timerRing;
        }

        try
        {
            return effect.Insistent.Deserialize<InsistentOptions>(_json) ?? _timerRing;
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Glucose watch {WatchId}'s insistent options do not read; ringing it as a timer", watch.Watch.Id);
            return _timerRing;
        }
    }

    // Both people are rung whatever the other's came to: one taking it is not the other told.
    private async Task<bool> RingOnChannelsAsync(FiredGlucoseWatch watch, GlucoseAnnounceEffect effect, CancellationToken ct)
    {
        var writer = new Account(watch.Subject.TenantId, watch.Watch.WriterUserId);
        Account[] people = writer == watch.Subject ? [watch.Subject] : [watch.Subject, writer];
        var taken = await Task.WhenAll(people.Select(person =>
            alerts.RingAsync(new ChannelAlertRequest(person, effect.Text, AnnounceKind.Alarm, watch.Watch.AgentId), ct)));
        if (taken.Any(rang => rang))
        {
            logger.LogInformation("Glucose watch {WatchId} rang on its people's channels", watch.Watch.Id);
            return true;
        }

        logger.LogWarning("Glucose watch {WatchId}'s alarm rang nowhere: no agent was listening to ring it on channels", watch.Watch.Id);
        return false;
    }
}