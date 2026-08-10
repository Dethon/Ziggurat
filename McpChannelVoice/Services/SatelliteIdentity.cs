using Domain.DTOs.Metrics;
using Domain.DTOs.Voice;
using McpChannelVoice.Settings;

namespace McpChannelVoice.Services;

// Which satellite something is about, said once. Every report the hub makes names a satellite by
// these three fields together, so a caller that says which satellite it means cannot name two of
// them and forget the last.
public readonly record struct SatelliteIdentity(string SatelliteId, string? Room, string? Identity)
{
    public static SatelliteIdentity Of(SatelliteSession session) =>
        new(session.SatelliteId, session.Config.Room, session.Config.Identity);

    // An offline target has no session: the registry's config is all there is to name it by. Null
    // config means the registry does not know the satellite, and the id alone is the whole identity.
    public static SatelliteIdentity Of(string satelliteId, SatelliteConfig? config) =>
        new(satelliteId, config?.Room, config?.Identity);

    // A decorator inside the STT chain is handed the satellite as three fields on its options rather
    // than the session, so it names the satellite through the same triple as everything else. An
    // unattributed call (no host filled these in) keeps the empty id it arrived with.
    public static SatelliteIdentity Of(TranscriptionOptions options) =>
        new(options.SatelliteId ?? string.Empty, options.Room, options.Identity);
}

// Stamping lives here rather than on VoiceEvent because the event is a Domain DTO and must not
// learn what a satellite session is. The voice server knows both.
public static class VoiceEventIdentity
{
    public static VoiceEvent About(this VoiceEvent evt, SatelliteIdentity identity) => evt with
    {
        // The satellites are one of three places speech now reaches whisper from, and this is the
        // one line every voice publish already passes through.
        Channel = "voice",
        SatelliteId = identity.SatelliteId,
        Room = identity.Room,
        Identity = identity.Identity
    };

    public static VoiceEvent About(this VoiceEvent evt, SatelliteSession session) =>
        evt.About(SatelliteIdentity.Of(session));
}