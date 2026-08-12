namespace Domain.DTOs.Metrics;

// The three places speech reaches whisper from, as VoiceEvent.Channel spells them. Dimension
// values are dashboard-visible strings rather than an enum, so the one thing to keep straight is
// that every publisher spells them from here.
public static class VoiceChannels
{
    public const string Satellite = "voice";
    public const string Web = "web";
    public const string Telegram = "telegram";
}