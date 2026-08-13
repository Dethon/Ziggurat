using System.Net;
using System.Runtime.InteropServices;
using Domain.Contracts;
using Domain.Tools.Config;
using Domain.Tools.Files;
using Infrastructure.Clients;
using Infrastructure.Clients.Bash;
using Infrastructure.Utils;
using Mcp.Hosting;
using McpServerSandbox.Settings;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Tests.Integration.Fixtures;

public class McpSandboxServerFixture : IAsyncLifetime
{
    private IHost _host = null!;

    public string McpEndpoint { get; private set; } = null!;
    public string SandboxRoot { get; private set; } = null!;
    public string HomeDir { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        Skip.IfNot(RuntimeInformation.IsOSPlatform(OSPlatform.Linux), "Sandbox integration tests require Linux bash");

        SandboxRoot = "/";
        HomeDir = Path.Combine(Path.GetTempPath(), $"mcp-sandbox-{Guid.NewGuid()}");
        Directory.CreateDirectory(HomeDir);

        var port = TestPort.GetAvailable();
        var settings = new McpSettings
        {
            ContainerRoot = SandboxRoot,
            HomeDir = HomeDir,
            DefaultTimeoutSeconds = 30,
            MaxTimeoutSeconds = 120,
            OutputCapBytes = 65536,
            AllowedExtensions = [".md", ".txt", ".py", ".sh", ".json"]
        };

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseKestrel(options =>
        {
            options.Listen(IPAddress.Loopback, port);
        });

        builder.Services
            .AddTransient<LibraryPathConfig>(_ => new LibraryPathConfig(settings.ContainerRoot))
            .AddTransient<IFileSystemClient, LocalFileSystemClient>()
            .AddSingleton(new BashRunnerOptions
            {
                ContainerRoot = settings.ContainerRoot,
                DefaultTimeoutSeconds = settings.DefaultTimeoutSeconds,
                MaxTimeoutSeconds = settings.MaxTimeoutSeconds,
                OutputCapBytes = settings.OutputCapBytes
            })
            .AddSingleton<ICommandRunner, BashRunner>()
            .AddSingleton(sp => new SandboxFileSystem(
                "sandbox",
                "Linux sandbox container.",
                sp.GetRequiredService<IFileSystemClient>(),
                new LibraryPathConfig(settings.ContainerRoot),
                settings.AllowedExtensions,
                sp.GetRequiredService<ICommandRunner>(),
                settings.HomeDir))
            .AddToolServer(settings, ToolResponse.Create)
            .AddFileSystemTools<SandboxFileSystem>()
            .AddFileSystemResource<SandboxFileSystem>();

        var app = builder.Build();
        app.MapMcp("/mcp");

        _host = app;
        await _host.StartAsync();

        McpEndpoint = $"http://localhost:{port}/mcp";
    }

    public async Task DisposeAsync()
    {
        if (_host is not null)
        {
            await _host.StopAsync();
            _host.Dispose();
        }

        try
        {
            if (HomeDir is not null && Directory.Exists(HomeDir))
            {
                Directory.Delete(HomeDir, true);
            }
        }
        catch
        {
            // Ignore cleanup errors
        }
    }
}