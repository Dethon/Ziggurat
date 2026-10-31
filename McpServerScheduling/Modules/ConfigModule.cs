using Domain.Agents;
using Domain.Contracts;
using Domain.Prompts;
using Domain.Tools.Scheduling.Vfs;
using Infrastructure.Clients.Accounts;
using Infrastructure.StateManagers;
using Infrastructure.Utils;
using Infrastructure.Validation;
using Mcp.Hosting;
using McpServerScheduling.McpPrompts;
using McpServerScheduling.Services;
using McpServerScheduling.Settings;
using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;

namespace McpServerScheduling.Modules;

public static class ConfigModule
{
    public static IServiceCollection ConfigureScheduling(this IServiceCollection services, SchedulingSettings settings)
    {
        services
            .AddSingleton<IConnectionMultiplexer>(_ => RedisConnection.ConnectResiliently(settings.RedisConnectionString))
            .AddSingleton<IScheduleStore, RedisScheduleStore>()
            .AddSingleton<ICronValidator, CronValidator>()
            .AddSingleton(TimeProvider.System)
            .AddSingleton<MutableAgentCatalog>()
            .AddSingleton<IAgentCatalog>(sp => sp.GetRequiredService<MutableAgentCatalog>())
            .AddSingleton<IMutableAgentCatalog>(sp => sp.GetRequiredService<MutableAgentCatalog>())
            .AddSingleton<ScheduleFileSystem>()
            .AddSingleton<ScheduleSetupSummary>()
            .AddHostedService<ScheduleDispatcherService>()
            // Each household's settings, for the zone its schedules are read and fired in: asked with
            // this server's own credential, and dropped when a save is announced on its Redis.
            .AddHouseholdConfiguration(settings.Accounts, settings.Mcp.SharedSecret);

        services
            .AddToolServer(settings, ToolResponse.Create)
            .WithTools<RegisterAgentsTool>()
            // Gate-on-live: the dispatcher deletes or advances a schedule only on a confirmed
            // delivery, so buffering on a failed emit would keep the record *and* leave a duplicate
            // behind — the schedule would fire twice.
            .AddChannelServer(DeliveryPolicy.GateOnLive, noOutboundSurface: true)
            .AddFileSystemTools<ScheduleFileSystem>()
            // The schedules' delegations, for the agent's redelegate and retire-signing-keys.
            .AddDelegationStore<ScheduleDelegationStore>()
            .AddFileSystemResource<ScheduleFileSystem>()
            // The skill that teaches this server's files ships beside them, in the zone the
            // server reads cron and runAt in.
            .AddSkills(SchedulingSkill.For(TimeZoneInfo.Local.Id))
            .WithPrompts<McpSystemPrompt>();

        return services;
    }
}