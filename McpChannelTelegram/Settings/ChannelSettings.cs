using Infrastructure.Clients.Transcription;

namespace McpChannelTelegram.Settings;

public record ChannelSettings
{
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

// Chat dictation gets its own section rather than sharing the voice channel's, whose short-phrase
// prompt and room placeholders are tuned for satellite commands and would skew a long spoken
// paragraph.
public record DictationSettings
{
    public TranscriptionClientConfig Transcription { get; init; } = new() { Language = "es" };

    // The same two minutes WebChat's recording stops itself at, checked against the duration
    // Telegram reports before the file is fetched.
    public TimeSpan MaxLength { get; init; } = TimeSpan.FromMinutes(2);

    // The gibberish gate the satellites already use: whisper answers a recording of nothing with a
    // plausible sentence it has seen in a thousand subtitle files. A null signal fails open.
    public double AvgLogProbThreshold { get; init; } = -1.0;
    public double NoSpeechProbThreshold { get; init; } = 0.6;
}