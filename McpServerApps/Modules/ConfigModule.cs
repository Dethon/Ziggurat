using Domain.Prompts;
using Infrastructure.Clients.Accounts;
using Infrastructure.Metrics;
using Infrastructure.Utils;
using Mcp.Hosting;
using McpServerApps.Asks;
using McpServerApps.Checks;
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
            // What an app's pages report went wrong, kept per app beside its repository.
            .AddSingleton<AppErrorLog>()
            .AddSingleton<AppErrorReports>()
            .AddAppPages(settings.Listeners)
            // The household's published apps, for its owners' spaces page on the accounts service.
            .AddPublishedAppsListing(settings.Listeners)
            .AddPersonsAppsListing(settings.Listeners)
            // A built app's reads, handed to the agent with this server's own credential (AgentReads).
            .AddHttpClient<AgentReads>(client => client.Timeout = TimeSpan.FromSeconds(30)).Services
            // A built app's asks, sent to its building agent on this server's own channel and waited on whole.
            .AddSingleton<AskConversations>()
            .AddSingleton<AgentAsks>()
            // A check runs the preview in a browser of its own, as the person whose turn asked.
            .AddSingleton<AppChecks>()
            .AddSingleton(sp => new AppsFileSystem(
                sp.GetRequiredService<BuiltAppStore>(), sp.GetRequiredService<AppChecks>(), settings.AppsDomain))
            // The calling household's agent names, asked with this server's own credential on the
            // call's token: only the household's own agents build apps. A save is heard on Redis when
            // the server has it; without, a renamed or added agent reaches it when its kept answer runs out.
            .AddHouseholdConfiguration(settings.Accounts, settings.Mcp.SharedSecret)
            // The app sign-in's code redeemed and an app session renewed, with the same credential, and
            // the `apps` assertions they answer verified against the accounts service's keys.
            .AddAccountResolver(settings.Accounts, settings.Mcp.SharedSecret)
            .AddAssertionVerifier(settings.Accounts.JwksUrl, settings.Mcp.TokenVerification);

        // The check browser's one way out, where it has a proxy to go through (in the deployment its network
        // has no other): the public internet alone.
        if (settings.Checks.Browser.Proxy.Length > 0)
        {
            services
                .AddSingleton<IEgressNetwork, SystemEgressNetwork>()
                .AddHostedService<CheckEgress>();
        }

        // Where every way a person's sessions end is announced, which ends their app sessions at once, and
        // the deployment's metrics feed, where a broken app is a household error the operator sees (and a
        // failed tool call too, by the shared filter). Connected when first asked, so a host run or a test
        // without Redis connects to nothing and publishes nothing.
        if (settings.RedisConnectionString.Length > 0)
        {
            services
                .AddSingleton<IConnectionMultiplexer>(_ => ConnectionMultiplexer.Connect(settings.RedisConnectionString))
                .AddHostedService<EndedAppSessionsListener>()
                .AddMetricsPublishing(AppErrorReports.ServiceName);
        }

        services
            .AddToolServer(settings, ToolResponse.Create)
            // A page waits on each ask's whole reply, and nobody there can approve a call.
            .AddAgentTurns()
            // Gate-on-live: an ask nobody is polling for is answered unavailable at once rather than
            // buffered for an agent that may never come.
            .AddChannelServer(DeliveryPolicy.GateOnLive)
            .AddFileSystemTools<AppsFileSystem>()
            .AddFileSystemResource<AppsFileSystem>()
            // How an app is built, checked and published, loaded on demand; offered only where /apps is.
            .AddSkills(AppsSkill.Text);

        return services;
    }
}