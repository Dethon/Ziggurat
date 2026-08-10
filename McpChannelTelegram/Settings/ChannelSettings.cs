using Infrastructure.Clients.Transcription;

namespace McpChannelTelegram.Settings;

public record ChannelSettings
{
    public required AgentBotConfig[] Bots { get; init; }
    public required string[] AllowedUsernames { get; init; }
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
}