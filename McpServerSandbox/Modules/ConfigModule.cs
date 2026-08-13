using Domain.Contracts;
using Domain.Tools.Config;
using Domain.Tools.Files;
using Infrastructure.Clients;
using Infrastructure.Clients.Bash;
using Infrastructure.Utils;
using Mcp.Hosting;
using McpServerSandbox.McpPrompts;
using McpServerSandbox.Settings;
using Microsoft.Extensions.DependencyInjection;

namespace McpServerSandbox.Modules;

public static class ConfigModule
{
    public static IServiceCollection ConfigureMcp(this IServiceCollection services, McpSettings settings)
    {
        services
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
                // The reusable disk root takes the mount's prose the same way it takes its name.
                "Linux sandbox container — supports command execution via fs_exec (bash, python3, "
                + "pip, git, curl, jq). Persistent /home/sandbox_user (named volume), ephemeral "
                + "system dirs, full outbound network, no inbound ports. See the Sandbox Filesystem "
                + "prompt for limits.",
                sp.GetRequiredService<IFileSystemClient>(),
                new LibraryPathConfig(settings.ContainerRoot),
                settings.AllowedExtensions,
                sp.GetRequiredService<ICommandRunner>(),
                settings.HomeDir))
            .AddToolServer(settings, ToolResponse.Create)
            .AddFileSystemTools<SandboxFileSystem>()
            .AddFileSystemResource<SandboxFileSystem>()
            .WithPrompts<McpSystemPrompt>();

        return services;
    }
}