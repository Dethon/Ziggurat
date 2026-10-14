using Domain.Contracts;
using Domain.Tools.Sugary.Vfs;
using Infrastructure.Utils;
using Mcp.Hosting;
using McpServerSugary.Settings;
using McpServerSugary.Store;
using Microsoft.Extensions.DependencyInjection;

namespace McpServerSugary.Modules;

public static class ConfigModule
{
    public static IServiceCollection ConfigureSugary(this IServiceCollection services, SugarySettings settings)
    {
        var homeZone = HomeZone(settings);

        services
            .AddSugaryStore(settings)
            .AddSingleton<ISugaryLog, SqliteSugaryLog>()
            .AddSingleton(sp => new SugaryFileSystem(sp.GetRequiredService<ISugaryLog>(), homeZone))
            .AddHostedService<SugaryDatabaseMigrator>();

        services
            .AddToolServer(settings, ToolResponse.Create)
            .AddFileSystemTools<SugaryFileSystem>()
            .AddFileSystemResource<SugaryFileSystem>();

        return services;
    }

    // Resolved at registration so an id the container does not know stops the server at startup,
    // before a single time is written on the wrong clock.
    public static TimeZoneInfo HomeZone(SugarySettings settings) =>
        string.IsNullOrWhiteSpace(settings.HomeZone)
            ? TimeZoneInfo.Local
            : TimeZoneInfo.FindSystemTimeZoneById(settings.HomeZone);
}