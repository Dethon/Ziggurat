using Domain.DTOs;
using Domain.Security;
using Infrastructure.Clients.Accounts;
using Mcp.Hosting;

namespace McpChannelSignalR.Settings;

public record ChannelSettings : IMcpHostSettings
{
    // This server's own credential, which its /mcp asks for (MCP__SHAREDSECRET).
    public McpGateSettings Mcp { get; init; } = new();

    // The browser origins the hub and the upload and dictation endpoints answer with credentials
    // (CORS__ALLOWEDORIGINS__n), the same section the Agent host binds.
    public CorsSettings Cors { get; init; } = new();

    public required string RedisConnectionString { get; init; }
    public WebPushConfig? WebPush { get; init; }
    public AttachmentSettings Attachments { get; init; } = new();
    public DictationSettings Dictation { get; init; } = new();

    // The accounts service, asked the name of each connection's account with this server's own
    // credential (Mcp:SharedSecret).
    public AccountsClientConfig Accounts { get; init; } = new();

    // The apps server's MCP listener, where WebChat's space switcher asks a person's apps (APPSURL, a
    // topology value compose sets); empty lists nobody's apps. A generic bound on that ask, so a slow apps
    // server never holds the switcher.
    public string AppsUrl { get; init; } = "";
    public TimeSpan AppsTimeout { get; init; } = TimeSpan.FromSeconds(3);

    // The same block the Agent host binds, so the two processes writing topics agree about how
    // long a conversation lives and where the archive begins.
    public RetentionSettings Retention { get; init; } = new();
}