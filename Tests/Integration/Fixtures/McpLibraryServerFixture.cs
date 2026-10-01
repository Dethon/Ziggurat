using System.Net;
using Domain.Contracts;
using Domain.Tools.Config;
using Domain.Tools.Downloads.Vfs;
using Domain.Tools.Files;
using Infrastructure.Clients;
using Infrastructure.StateManagers;
using Infrastructure.Utils;
using Mcp.Hosting;
using McpServerLibrary.McpTools;
using McpServerLibrary.Settings;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Tests.Integration.Fixtures;

public class McpLibraryServerFixture : IAsyncLifetime
{
    private IHost _host = null!;
    private int _port;
    private IMemoryCache _cache = null!;

    private JackettFixture Jackett { get; } = new();
    private QBittorrentFixture QBittorrent { get; } = new();

    public string McpEndpoint { get; private set; } = null!;
    public string LibraryPath { get; private set; } = null!;
    public string DownloadPath { get; private set; } = null!;
    public InMemoryDownloadRoutingStore RoutingStore { get; } = new();

    public async Task InitializeAsync()
    {
        await Task.WhenAll(Jackett.InitializeAsync(), QBittorrent.InitializeAsync());

        LibraryPath = Path.Combine(Path.GetTempPath(), $"mcp-library-{Guid.NewGuid()}");
        DownloadPath = Path.Combine(LibraryPath, "downloads");
        Directory.CreateDirectory(LibraryPath);
        Directory.CreateDirectory(DownloadPath);

        _cache = new MemoryCache(new MemoryCacheOptions());
        _port = TestPort.GetAvailable();

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseKestrel(options =>
        {
            options.Listen(IPAddress.Loopback, _port);
        });

        builder.Services
            .AddSingleton<DownloadPathConfig>(_ => new DownloadPathConfig(DownloadPath))
            .AddSingleton<LibraryPathConfig>(_ => new LibraryPathConfig(LibraryPath))
            .AddSingleton(_cache)
            .AddSingleton<IDownloadRoutingStore>(RoutingStore)
            .AddSingleton<ISearchResultsManager, SearchResultsManager>()
            .AddSingleton<ISearchClient>(_ => Jackett.CreateClient())
            .AddSingleton<IDownloadClient>(_ => QBittorrent.CreateClient())
            .AddSingleton<IFileSystemClient, LocalFileSystemClient>()
            .AddSingleton<DownloadsOverlay>()
            .AddSingleton(sp => new MediaLibraryDiskFileSystem(
                sp.GetRequiredService<IFileSystemClient>(),
                new LibraryPathConfig(LibraryPath),
                sp.GetRequiredService<DownloadsOverlay>()))
            .AddToolServer(
                new McpSettings
                {
                    Jackett = new JackettConfiguration { ApiKey = "unused", ApiUrl = "http://localhost" },
                    QBittorrent = new QBittorrentConfiguration
                    {
                        ApiUrl = "http://localhost",
                        UserName = "unused",
                        Password = "unused"
                    },
                    DownloadLocation = DownloadPath,
                    BaseLibraryPath = LibraryPath,
                    RedisConnectionString = "unused",
                    Mcp = McpTestSecret.Gate
                },
                ToolResponse.Create)
            .WithTools<McpFileSearchTool>()
            .WithTools<McpFileDownloadTool>()
            .AddFileSystemTools<MediaLibraryDiskFileSystem>()
            .AddFileSystemResource<MediaLibraryDiskFileSystem>();

        var app = builder.Build();
        app.MapMcp("/mcp");

        _host = app;
        await _host.StartAsync();

        McpEndpoint = $"http://localhost:{_port}/mcp";
    }

    public void CreateLibraryStructure(string relativePath)
    {
        var fullPath = Path.Combine(LibraryPath, relativePath);
        Directory.CreateDirectory(fullPath);
    }

    public void CreateLibraryFile(string relativePath, string content = "test content")
    {
        var fullPath = Path.Combine(LibraryPath, relativePath);
        var directory = Path.GetDirectoryName(fullPath);
        if (directory != null)
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(fullPath, content);
    }

    public void CreateDownloadFile(string relativePath, string content = "downloaded content")
    {
        var fullPath = Path.Combine(DownloadPath, relativePath);
        var directory = Path.GetDirectoryName(fullPath);
        if (directory != null)
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(fullPath, content);
    }

    public bool FileExistsInLibrary(string relativePath)
    {
        return File.Exists(Path.Combine(LibraryPath, relativePath));
    }

    public bool FileExistsInDownloads(string relativePath)
    {
        return File.Exists(Path.Combine(DownloadPath, relativePath));
    }

    public async Task DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
        _cache.Dispose();

        await Jackett.DisposeAsync();
        await QBittorrent.DisposeAsync();

        try
        {
            if (Directory.Exists(LibraryPath))
            {
                Directory.Delete(LibraryPath, true);
            }

            if (Directory.Exists(DownloadPath))
            {
                Directory.Delete(DownloadPath, true);
            }
        }
        catch
        {
            // Ignore cleanup errors
        }
    }
}