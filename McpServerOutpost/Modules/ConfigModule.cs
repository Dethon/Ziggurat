using Domain.Contracts;
using Domain.Tools.Config;
using Domain.Tools.Files;
using Infrastructure.Clients;
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
            .AddTransient<IFileSystemClient, LocalFileSystemClient>()
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
}