using Domain.Security;
using Infrastructure.Clients.Accounts;
using Infrastructure.Clients.Transcription;
using Mcp.Hosting;

namespace McpChannelTelegram.Settings;

public record ChannelSettings : IMcpHostSettings
{
    // This server's own credential, which its /mcp asks for (MCP__SHAREDSECRET).
    public McpGateSettings Mcp { get; init; } = new();

    public required AgentBotConfig[] Bots { get; init; }

    // The accounts service, asked with this server's own credential (Mcp:SharedSecret) whose account
    // each sender's numeric Telegram id is linked to, and the ways in the bot redeems.
    public AccountsClientConfig Accounts { get; init; } = new();

    public LinkCacheSettings LinkCache { get; init; } = new();

    // Metrics only: this channel keeps no state of its own (a Telegram file id is the store), so
    // Redis is here for the same reason the voice hub has it — the metric sink.
    public string RedisConnectionString { get; init; } = "redis:6379";
    public DictationSettings Dictation { get; init; } = new();
}

// How long the bot believes what the accounts service said about a sender or a group. A link rarely
// changes, so an answer naming somebody is kept for minutes; a refusal only for seconds, so somebody
// linked a moment ago is let in at once while a stranger's every message still costs no request.
public record LinkCacheSettings
{
    public TimeSpan Linked { get; init; } = TimeSpan.FromMinutes(5);
    public TimeSpan Unlinked { get; init; } = TimeSpan.FromSeconds(5);
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