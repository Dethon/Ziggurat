using Domain.Contracts;
using Domain.Tools.Config;
using Domain.Tools.Downloads.Vfs;
using Domain.Tools.Files;
using Infrastructure.StateManagers;
using Infrastructure.Utils;
using Mcp.Hosting;
using McpServerLibrary.McpPrompts;
using McpServerLibrary.McpTools;
using McpServerLibrary.Services;
using McpServerLibrary.Settings;
using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;

namespace McpServerLibrary.Modules;

public static class ConfigModule
{
    public static IServiceCollection ConfigureMcp(this IServiceCollection services, McpSettings settings)
    {
        services
            .AddMemoryCache()
            .AddTransient<DownloadPathConfig>(_ => new DownloadPathConfig(settings.DownloadLocation))
            .AddTransient<LibraryPathConfig>(_ => new LibraryPathConfig(settings.BaseLibraryPath))
            .AddSingleton<IConnectionMultiplexer>(_ => RedisConnection.ConnectResiliently(settings.RedisConnectionString))
            .AddSingleton<IDownloadRoutingStore, RedisDownloadRoutingStore>()
            .AddSingleton<ISearchResultsManager, SearchResultsManager>()
            .AddJacketClient(settings)
            .AddQBittorrentClient(settings)
            .AddFileSystemClient()
            .AddSingleton<DownloadsOverlay>()
            .AddSingleton(sp => new MediaLibraryDiskFileSystem(
                sp.GetRequiredService<IFileSystemClient>(),
                new LibraryPathConfig(settings.BaseLibraryPath),
                sp.GetRequiredService<DownloadsOverlay>()))
            .AddHostedService<DownloadCompletionWatcher>()
            .AddToolServer(settings, ToolResponse.Create)
            .WithTools<McpFileSearchTool>()
            .WithTools<McpFileDownloadTool>()
            // A channel-protocol tool (invoked by the agent's channel connection, hidden from the
            // LLM). This server's own no-op, not the shared catalog-writing one: the library
            // channel does not target agents, so the set is ignored.
            .WithTools<McpTools.RegisterAgentsTool>()
            // Gate-on-live: the completion watcher drops a routing entry only on a confirmed
            // delivery, so a disconnected-but-still-buffering subscriber must not read as delivered.
            .AddChannelServer(DeliveryPolicy.GateOnLive, noOutboundSurface: true)
            .AddFileSystemTools<MediaLibraryDiskFileSystem>()
            .WithPrompts<McpSystemPrompt>()
            .AddFileSystemResource<MediaLibraryDiskFileSystem>();

        return services;
    }
}