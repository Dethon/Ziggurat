using Domain.DTOs;

namespace McpChannelSignalR.Settings;

public record ChannelSettings
{
    public required string RedisConnectionString { get; init; }
    public WebPushConfig? WebPush { get; init; }
    public AttachmentSettings Attachments { get; init; } = new();
    public DictationSettings Dictation { get; init; } = new();

    // The same block the Agent host binds, so the two processes writing topics agree about how
    // long a conversation lives and where the archive begins.
    public RetentionSettings Retention { get; init; } = new();
}

public record WebPushConfig
{
    public string? PublicKey { get; init; }
    public string? PrivateKey { get; init; }
    public string? Subject { get; init; }

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(PublicKey)
        && !string.IsNullOrWhiteSpace(PrivateKey)
        && !string.IsNullOrWhiteSpace(Subject);
}