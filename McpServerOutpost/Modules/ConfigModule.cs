using Domain.Contracts;
using Domain.Tools.Config;
using Domain.Tools.Files;
using Infrastructure.Clients;
using Infrastructure.Clients.Bash;
using Infrastructure.Utils;
using Mcp.Hosting;
using McpServerOutpost.Settings;
using Microsoft.Extensions.DependencyInjection;

namespace McpServerOutpost.Modules;

public static class ConfigModule
{
    public static IServiceCollection ConfigureMcp(this IServiceCollection services, OutpostSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var root = new LibraryPathConfig(OutpostFileSystem.MountRoot);
        services
            .AddTransient<LibraryPathConfig>(_ => root)
            .AddTransient<IFileSystemClient, LocalFileSystemClient>();

        // One of two backend types, never one type with a flag. Capability is declared by
        // overriding and the registrar reflects over the type, so a backend that overrides exec
        // advertises exec whatever it was constructed with — the choice has to be which type gets
        // registered. Off is the default, because exposing somebody's files must not imply
        // exposing a shell on their computer.
        return settings.Exec
            ? services.AddExecuting(settings, root)
            : services.AddReadOnly(settings);
    }

    private static IServiceCollection AddReadOnly(this IServiceCollection services, OutpostSettings settings)
    {
        services
            .AddSingleton(sp => new OutpostFileSystem(
                settings.Name,
                sp.GetRequiredService<IFileSystemClient>(),
                settings.WorkingDirectory,
                settings.AllowedExtensions(),
                settings.Jailed))
            .AddToolServer(settings, ToolResponse.Create)
            .AddFileSystemTools<OutpostFileSystem>()
            .AddFileSystemResource<OutpostFileSystem>();

        return services;
    }

    private static IServiceCollection AddExecuting(
        this IServiceCollection services, OutpostSettings settings, LibraryPathConfig root)
    {
        services
            .AddSingleton(new BashRunnerOptions
            {
                // The machine's root, so a command's working directory is spelled the way every
                // other path on this mount is. The confinement, where there is one, is the
                // outpost's own rule rather than this runner's.
                ContainerRoot = root.BaseLibraryPath,
                DefaultTimeoutSeconds = settings.DefaultTimeoutSeconds,
                MaxTimeoutSeconds = settings.MaxTimeoutSeconds,
                OutputCapBytes = settings.OutputCapBytes
            })
            .AddSingleton<ICommandRunner, BashRunner>()
            .AddSingleton(sp => new ExecutingOutpostFileSystem(
                settings.Name,
                sp.GetRequiredService<IFileSystemClient>(),
                settings.WorkingDirectory,
                settings.AllowedExtensions(),
                settings.Jailed,
                sp.GetRequiredService<ICommandRunner>()))
            .AddToolServer(settings, ToolResponse.Create)
            .AddFileSystemTools<ExecutingOutpostFileSystem>()
            .AddFileSystemResource<ExecutingOutpostFileSystem>();

        return services;
    }
}