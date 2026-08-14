using Domain.DTOs;

namespace McpChannelVoice.Settings;

public record VoiceSettings
{
    // Agent that handles voice transcripts. Sent as the message AgentId so the agent
    // host resolves this definition; null falls back to the first configured agent.
    public string? AgentId { get; init; }
    public string RedisConnectionString { get; init; } = "redis:6379";
    public WyomingClientSettings WyomingClient { get; init; } = new();
    public SttSettings Stt { get; init; } = new();
    public TtsSettings Tts { get; init; } = new();
    public AnnounceSettings Announce { get; init; } = new();
    public Dictionary<string, SatelliteConfig> Satellites { get; init; } = new();
    public TimeSpan ConversationLifetime { get; init; } = TimeSpan.FromMinutes(5);
    public FollowUpSettings FollowUp { get; init; } = new();
    public SpeakerVerificationSettings SpeakerVerification { get; init; } = new();
    public TseSettings Tse { get; init; } = new();
    public ArbitrationSettings Arbitration { get; init; } = new();
    public CommandSettings Commands { get; init; } = new();

    // The same block the Agent and chat hosts bind. This channel writes conversations and never
    // reads the list, so only the purge horizon reaches anything here — but it has to be the same
    // number, or a topic voice wrote would age on a different clock from one anybody else wrote.
    public RetentionSettings Retention { get; init; } = new();

    // Channel-wide default geographic locality (e.g. "Madrid, Spain"). Satellites that don't set
    // their own Locality inherit this one. Surfaced to the agent via SatelliteConfig.DisplayLocation.
    public string? Locality { get; init; }

    // Bakes the global Locality default into every satellite that lacks its own, so the rest of the
    // app reads a single resolved value off SatelliteConfig.Locality. Applied once at settings load.
    public VoiceSettings WithResolvedLocalityDefaults()
    {
        if (string.IsNullOrWhiteSpace(Locality))
        {
            return this;
        }

        var resolved = Satellites.ToDictionary(
            kv => kv.Key,
            kv => string.IsNullOrWhiteSpace(kv.Value.Locality)
                ? kv.Value with { Locality = Locality }
                : kv.Value);

        return this with { Satellites = resolved };
    }
}