using Infrastructure.Clients.Accounts;
using Infrastructure.Utils;
using Mcp.Hosting;
using McpServerApps.Serving;
using McpServerApps.Settings;
using McpServerApps.Store;
using McpServerApps.Vfs;
using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;

namespace McpServerApps.Modules;

public static class ConfigModule
{
    public static IServiceCollection ConfigureApps(this IServiceCollection services, AppsSettings settings)
    {
        services
            .AddSingleton(TimeProvider.System)
            .AddSingleton(sp => new BuiltAppStore(settings, sp.GetRequiredService<TimeProvider>()))
            .AddSingleton<AppSessions>()
            .AddSingleton<AppViewers>()
            .AddAppPages(settings.Listeners)
            // A built app's reads, handed to the agent with this server's own credential (AgentReads).
            .AddHttpClient<AgentReads>(client => client.Timeout = TimeSpan.FromSeconds(30)).Services
            .AddSingleton(sp => new AppsFileSystem(sp.GetRequiredService<BuiltAppStore>(), settings.AppsDomain))
            // The calling household's agent names, asked with this server's own credential on the
            // call's token: only the household's own agents build apps. A save is heard on Redis when
            // the server has it; without, a renamed or added agent reaches it when its kept answer runs out.
            .AddHouseholdConfiguration(settings.Accounts, settings.Mcp.SharedSecret)
            // The app sign-in's code redeemed and an app session renewed, with the same credential, and
            // the `apps` assertions they answer verified against the accounts service's keys.
            .AddAccountResolver(settings.Accounts, settings.Mcp.SharedSecret)
            .AddAssertionVerifier(settings.Accounts.JwksUrl, settings.Mcp.TokenVerification);

        // Where every way a person's sessions end is announced, which ends their app sessions at once.
        // Connected when first asked, so a host run or a test without Redis connects to nothing.
        if (settings.RedisConnectionString.Length > 0)
        {
            services
                .AddSingleton<IConnectionMultiplexer>(_ => ConnectionMultiplexer.Connect(settings.RedisConnectionString))
                .AddHostedService<EndedAppSessionsListener>();
        }

        services
            .AddToolServer(settings, ToolResponse.Create)
            .AddFileSystemTools<AppsFileSystem>()
            .AddFileSystemResource<AppsFileSystem>();

        return services;
    }
}