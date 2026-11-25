using Infrastructure.Clients.Accounts;
using Infrastructure.Utils;
using Mcp.Hosting;
using McpServerApps.Settings;
using McpServerApps.Store;
using McpServerApps.Vfs;
using Microsoft.Extensions.DependencyInjection;

namespace McpServerApps.Modules;

public static class ConfigModule
{
    public static IServiceCollection ConfigureApps(this IServiceCollection services, AppsSettings settings)
    {
        services
            .AddSingleton(TimeProvider.System)
            .AddSingleton(sp => new BuiltAppStore(settings, sp.GetRequiredService<TimeProvider>()))
            .AddSingleton(sp => new AppsFileSystem(sp.GetRequiredService<BuiltAppStore>(), settings.AppsDomain))
            // The calling household's agent names, asked with this server's own credential on the
            // call's token: only the household's own agents build apps. No Redis here, so a renamed
            // or added agent reaches it when its kept answer runs out.
            .AddHouseholdConfiguration(settings.Accounts, settings.Mcp.SharedSecret);

        services
            .AddToolServer(settings, ToolResponse.Create)
            .AddFileSystemTools<AppsFileSystem>()
            .AddFileSystemResource<AppsFileSystem>();

        return services;
    }
}