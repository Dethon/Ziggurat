using Domain.Security;
using Infrastructure.Clients.Accounts;
using Infrastructure.Clients.Transcription;
using Mcp.Hosting;

namespace McpChannelTelegram.Settings;

public record ChannelSettings : IMcpHostSettings
{
    // This server's own credential, which its /mcp asks for (MCP__SHAREDSECRET).
    public McpGateSettings Mcp { get; init; } = new();

    public BotSettings Bots { get; init; } = new();

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

// The bots this server polls. The shared bot is the deployment's and serves every household (its
// token a secret; empty runs none); every household's own bots are its configuration's, read across
// households this often. The reader keeps that answer for a minute and drops it the moment any
// household's keys are saved, so asking often costs nothing and a saved bot starts within moments.
public record BotSettings
{
    public string SharedToken { get; init; } = "";
    public TimeSpan Refresh { get; init; } = TimeSpan.FromSeconds(5);
}

// What only Telegram needs on top of the shared chat settings: the gibberish gate the satellites
// already use — whisper answers a recording of nothing with a plausible sentence it has seen in a
// thousand subtitle files. A null signal fails open.
public record DictationSettings : ChatDictationSettings
{
    public double AvgLogProbThreshold { get; init; } = -1.0;
    public double NoSpeechProbThreshold { get; init; } = 0.6;
}