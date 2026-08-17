using Domain.Contracts;
using Domain.DTOs;
using Domain.Tools.Config;
using Domain.Tools.Files;
using Infrastructure.Clients;
using Infrastructure.Clients.Bash;
using Infrastructure.Utils;
using Mcp.Hosting;
using McpServerOutpost.Registration;
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
        services = settings.Exec
            ? services.AddExecuting(settings, root)
            : services.AddReadOnly(settings);

        return services.AddRegistration(settings);
    }

    // Registering is optional at this layer, not because announcing yourself is a nicety but
    // because an outpost that is named in an agent's configured endpoints by hand needs no hub at
    // all — that is how one was reached before it could announce itself, and it is still a valid
    // way to run one.
    private static IServiceCollection AddRegistration(
        this IServiceCollection services, OutpostSettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.Hub))
        {
            return services;
        }

        // Resolved here, at startup, so an address the hub could never dial is a failure to start
        // with a message that says so rather than a registration of something unreachable.
        var registration = new OutpostRegistration
        {
            Name = settings.Name,
            Endpoint = OutpostAddress.Resolve(settings.Hub, settings.Advertise, settings.Port)
        };

        return services
            .AddSingleton(registration)
            .AddSingleton(TimeProvider.System)
            .AddSingleton<IOutpostAnnouncer>(_ => new HttpOutpostAnnouncer(
                new HttpClient { BaseAddress = HubBase(settings.Hub), Timeout = TimeSpan.FromSeconds(10) },
                settings.SharedSecret))
            .AddHostedService<OutpostRegistrar>();
    }

    // The hub is given as the address of the agent, with or without a trailing slash; the three
    // outpost routes hang off it. A relative route against a base with no trailing slash would
    // replace the last segment, so it is added rather than assumed.
    private static Uri HubBase(string hub) =>
        new(hub.EndsWith('/') ? hub : hub + "/");

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