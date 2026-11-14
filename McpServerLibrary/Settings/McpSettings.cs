using Domain.Security;
using Infrastructure.Metrics;
using JetBrains.Annotations;
using Mcp.Hosting;

namespace McpServerLibrary.Settings;

public record McpSettings : IMcpHostSettings
{
    // Its heartbeat, and its helpers', posted on its tunnel's metrics back-call (METRICS__URL): the
    // deployment's dashboard has no other way to see it. Unset outside a kit, which posts nothing.
    public MetricsFeedConfig Metrics { get; init; } = new();

    // This server's own credential, which its /mcp asks for (MCP__SHAREDSECRET).
    public McpGateSettings Mcp { get; init; } = new();

    public required JackettConfiguration Jackett { get; init; }
    public required QBittorrentConfiguration QBittorrent { get; init; }
    public required string DownloadLocation { get; init; }
    public required string BaseLibraryPath { get; init; }
    public required string RedisConnectionString { get; init; }
    public int CompletionPollSeconds { get; init; } = 5;
}

public record JackettConfiguration
{
    public required string ApiKey { get; [UsedImplicitly] init; }
    public required string ApiUrl { get; [UsedImplicitly] init; }
    public int SearchDeadlineSeconds { get; [UsedImplicitly] init; } = 10;
}

public record QBittorrentConfiguration
{
    public required string ApiUrl { get; [UsedImplicitly] init; }
    public required string UserName { get; [UsedImplicitly] init; }
    public required string Password { get; [UsedImplicitly] init; }
}