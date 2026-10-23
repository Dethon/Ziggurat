using Domain.DTOs;
using Domain.Security;
using Mcp.Hosting;

namespace McpChannelSignalR.Settings;

public record ChannelSettings : IMcpHostSettings
{
    // The deployment secret this server's /mcp asks for (MCP__SHAREDSECRET).
    public McpGateSettings Mcp { get; init; } = new();

    public required string RedisConnectionString { get; init; }
    public WebPushConfig? WebPush { get; init; }
    public AttachmentSettings Attachments { get; init; } = new();
    public DictationSettings Dictation { get; init; } = new();

    // The same block the Agent host binds, so the two processes writing topics agree about how
    // long a conversation lives and where the archive begins.
    public RetentionSettings Retention { get; init; } = new();
}