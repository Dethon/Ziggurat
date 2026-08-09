namespace McpChannelSignalR.Settings;

public record ChannelSettings
{
    public required string RedisConnectionString { get; init; }
    public WebPushConfig? WebPush { get; init; }
    public AttachmentSettings Attachments { get; init; } = new();
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