using Domain.Security;
using Infrastructure.Clients.Transcription;
using Mcp.Hosting;

namespace McpChannelTelegram.Settings;

public record ChannelSettings : IMcpHostSettings
{
    // The deployment secret this server's /mcp asks for (MCP__SHAREDSECRET).
    public McpGateSettings Mcp { get; init; } = new();

    public required AgentBotConfig[] Bots { get; init; }
    public required string[] AllowedUsernames { get; init; }

    // Metrics only: this channel keeps no state of its own (a Telegram file id is the store), so
    // Redis is here for the same reason the voice hub has it — the metric sink.
    public string RedisConnectionString { get; init; } = "redis:6379";
    public DictationSettings Dictation { get; init; } = new();
}

public record AgentBotConfig
{
    public required string AgentId { get; init; }
    public required string BotToken { get; init; }
}

// What only Telegram needs on top of the shared chat settings: the gibberish gate the satellites
// already use — whisper answers a recording of nothing with a plausible sentence it has seen in a
// thousand subtitle files. A null signal fails open.
public record DictationSettings : ChatDictationSettings
{
    public double AvgLogProbThreshold { get; init; } = -1.0;
    public double NoSpeechProbThreshold { get; init; } = 0.6;
}