using System.ComponentModel;
using System.Net;
using System.Text.Json;
using Domain.Contracts;
using Domain.Security;
using Domain.Tools.Config;
using Domain.Tools.Downloads.Vfs;
using Domain.Tools.Files;
using Infrastructure.Clients;
using Infrastructure.Utils;
using Mcp.Hosting;
using McpServerVault.Settings;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ModelContextProtocol.Server;
using static Tests.Unit.Domain.Downloads.Vfs.DownloadFakes;

namespace Tests.Integration.Fixtures;

[CollectionDefinition("MultiFileSystem")]
public class MultiFileSystemCollection : ICollectionFixture<MultiFileSystemFixture> { }

public class MultiFileSystemFixture : IAsyncLifetime
{
    private IHost _libraryHost = null!;
    private IHost _notesHost = null!;
    private IHost _mediaHost = null!;

    public string LibraryEndpoint { get; private set; } = null!;
    public string NotesEndpoint { get; private set; } = null!;
    public string MediaEndpoint { get; private set; } = null!;
    public string LibraryPath { get; private set; } = null!;
    public string NotesPath { get; private set; } = null!;
    public string MediaPath { get; private set; } = null!;

    // The third host's download manager. A download is live for as long as this says so, which is
    // all the media mount's rule asks about — no torrent client, no containers.
    public FakeDownloadClient Downloads { get; } = new();

    public async Task InitializeAsync()
    {
        LibraryPath = Path.Combine(Path.GetTempPath(), $"mcp-library-{Guid.NewGuid()}");
        NotesPath = Path.Combine(Path.GetTempPath(), $"mcp-notes-{Guid.NewGuid()}");
        MediaPath = Path.Combine(Path.GetTempPath(), $"mcp-media-{Guid.NewGuid()}");
        Directory.CreateDirectory(LibraryPath);
        Directory.CreateDirectory(NotesPath);
        Directory.CreateDirectory(MediaPath);

        var libraryPort = TestPort.GetAvailable();
        var notesPort = TestPort.GetAvailable();
        var mediaPort = TestPort.GetAvailable();

        _libraryHost = BuildVaultHost(libraryPort, LibraryPath, builder => builder
            .WithResources<LibraryFileSystemResource>());

        _notesHost = BuildVaultHost(notesPort, NotesPath, builder => builder
            .WithResources<NotesFileSystemResource>());

        _mediaHost = BuildMediaHost(mediaPort);

        await Task.WhenAll(_libraryHost.StartAsync(), _notesHost.StartAsync(), _mediaHost.StartAsync());

        LibraryEndpoint = $"http://localhost:{libraryPort}/mcp";
        NotesEndpoint = $"http://localhost:{notesPort}/mcp";
        MediaEndpoint = $"http://localhost:{mediaPort}/mcp";
    }

    // The media library as its own server, so the topology every cross-mount bug lives in — a mount
    // with refusals of its own, reached through the real proxy, alongside a plain filesystem — is
    // finally something a test can build. The shipped ConfigModule is not reusable here: it wants
    // Redis, qBittorrent and Jackett. So this repeats its two filesystem registrations, and the
    // conformance tests are what hold the real module to the same pair.
    private IHost BuildMediaHost(int port)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseKestrel(options => options.Listen(IPAddress.Loopback, port));

        builder.Services
            .AddTransient<IFileSystemClient, LocalFileSystemClient>()
            .AddSingleton<IDownloadClient>(Downloads)
            .AddSingleton<IDownloadRoutingStore>(new FakeRoutingStore())
            .AddSingleton(sp => new DownloadsOverlay(
                sp.GetRequiredService<IDownloadClient>(),
                sp.GetRequiredService<IDownloadRoutingStore>(),
                sp.GetRequiredService<IFileSystemClient>(),
                new LibraryPathConfig(MediaPath)))
            .AddSingleton(sp => new MediaLibraryDiskFileSystem(
                sp.GetRequiredService<IFileSystemClient>(),
                new LibraryPathConfig(MediaPath),
                sp.GetRequiredService<DownloadsOverlay>()))
            .AddToolServer(new MediaHostSettings(MediaPath), ToolResponse.Create)
            .AddFileSystemTools<MediaLibraryDiskFileSystem>()
            .AddFileSystemResource<MediaLibraryDiskFileSystem>();

        var app = builder.Build();
        app.MapMcp("/mcp");
        return app;
    }

    private sealed record MediaHostSettings(string BaseLibraryPath) : IMcpHostSettings
    {
        public McpGateSettings Mcp => McpTestSecret.Gate;
    }

    private static IHost BuildVaultHost(int port, string vaultPath, Func<IMcpServerBuilder, IMcpServerBuilder> addResources)
    {
        var settings = new McpSettings
        {
            VaultPath = vaultPath,
            AllowedExtensions = [".md", ".txt", ".json"],
            Mcp = McpTestSecret.Gate
        };

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseKestrel(options =>
        {
            options.Listen(IPAddress.Loopback, port);
        });

        var mcpBuilder = builder.Services
            .AddTransient<LibraryPathConfig>(_ => new LibraryPathConfig(settings.VaultPath))
            .AddTransient<IFileSystemClient, LocalFileSystemClient>()
            .AddSingleton(sp => new TextDiskFileSystem(
                "vault",
                "Personal Obsidian vault.",
                sp.GetRequiredService<IFileSystemClient>(),
                new LibraryPathConfig(settings.VaultPath),
                settings.AllowedExtensions))
            .AddToolServer(settings, ToolResponse.Create)
            .AddFileSystemTools<TextDiskFileSystem>();

        addResources(mcpBuilder);

        var app = builder.Build();
        app.MapMcp("/mcp");
        return app;
    }

    public void CreateLibraryFile(string relativePath, string content = "test content")
    {
        CreateFile(LibraryPath, relativePath, content);
    }

    public void CreateNotesFile(string relativePath, string content = "test content")
    {
        CreateFile(NotesPath, relativePath, content);
    }

    private static void CreateFile(string basePath, string relativePath, string content)
    {
        var fullPath = Path.Combine(basePath, relativePath);
        var directory = Path.GetDirectoryName(fullPath);
        if (directory != null)
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(fullPath, content);
    }

    public void CreateMediaFile(string relativePath, string content = "test content")
    {
        CreateFile(MediaPath, relativePath, content);
    }

    public async Task DisposeAsync()
    {
        await Task.WhenAll(_libraryHost.StopAsync(), _notesHost.StopAsync(), _mediaHost.StopAsync());
        _libraryHost.Dispose();
        _notesHost.Dispose();
        _mediaHost.Dispose();

        foreach (var path in new[] { LibraryPath, NotesPath, MediaPath })
        {
            try
            {
                if (Directory.Exists(path))
                {
                    Directory.Delete(path, true);
                }
            }
            catch
            {
                // Ignore cleanup errors
            }
        }
    }
}

[McpServerResourceType]
public class LibraryFileSystemResource(McpSettings settings)
{
    [McpServerResource(
        UriTemplate = "filesystem://library",
        Name = "Library Filesystem",
        MimeType = "application/json")]
    [Description("Personal document library filesystem")]
    public string GetLibraryInfo()
    {
        return JsonSerializer.Serialize(new
        {
            name = "library",
            mountPoint = "/library",
            description = $"Personal document library ({settings.VaultPath})"
        });
    }
}

[McpServerResourceType]
public class NotesFileSystemResource(McpSettings settings)
{
    [McpServerResource(
        UriTemplate = "filesystem://notes",
        Name = "Notes Filesystem",
        MimeType = "application/json")]
    [Description("Personal notes filesystem")]
    public string GetNotesInfo()
    {
        return JsonSerializer.Serialize(new
        {
            name = "notes",
            mountPoint = "/notes",
            description = $"Personal notes ({settings.VaultPath})"
        });
    }
}