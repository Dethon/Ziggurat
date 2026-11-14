using Domain.DTOs;
using Domain.Security;
using Infrastructure.Clients.Accounts;
using Infrastructure.Clients.Topics;
using Infrastructure.Judgments;
using Infrastructure.Metrics;
using Mcp.Hosting;

namespace McpChannelVoice.Settings;

public record VoiceSettings : IMcpHostSettings
{
    // This server's own credential, which its /mcp asks for (MCP__SHAREDSECRET).
    public McpGateSettings Mcp { get; init; } = new();

    // Where a recognised speaker's profile is resolved to their account, asked with Mcp's credential.
    public AccountsClientConfig Accounts { get; init; } = new();

    // Where the agent mints the topic each new conversation opens under.
    public AgentClientConfig Agent { get; init; } = new();

    // The agent a satellite's words are for, where an operator pins one. Null — the default — names
    // none, and the agent sends each turn to the voice agent of the household it is for, the one its
    // owner marked on the Agents page (HouseholdAgents.VoiceAgent).
    public string? AgentId { get; init; }
    // The metrics feed's alone, and unused when Metrics names a URL: a household's voice posts its
    // events on its metrics back-call (METRICS__URL) and reaches no deployment database.
    public string RedisConnectionString { get; init; } = "redis:6379";
    public MetricsFeedConfig Metrics { get; init; } = new();
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

    // Jev, the hosted judge this channel asks what a spoken approval answer meant. It is asked
    // through OpenRouter, so the key is OPENROUTER__APIKEY, the same line the agent reads; empty
    // means the answer is read by the word list alone, never a startup failure.
    public OpenRouterKey OpenRouter { get; init; } = new();
    public TypeSafeOptions TypeSafe { get; init; } = new();
    public ApprovalSettings Approval { get; init; } = new();

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