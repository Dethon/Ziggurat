using Domain.Agents;
using Domain.Contracts;
using Domain.Prompts;
using Domain.Tools.Scheduling.Vfs;
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
            .AddHostedService<ScheduleDispatcherService>();

        services
            .AddToolServer(settings, ToolResponse.Create)
            .WithTools<RegisterAgentsTool>()
            // Gate-on-live: the dispatcher deletes or advances a schedule only on a confirmed
            // delivery, so buffering on a failed emit would keep the record *and* leave a duplicate
            // behind — the schedule would fire twice.
            .AddChannelServer(DeliveryPolicy.GateOnLive, noOutboundSurface: true)
            .AddFileSystemTools<ScheduleFileSystem>()
            .AddFileSystemResource<ScheduleFileSystem>()
            .WithPrompts<McpSystemPrompt>();

        return services;
    }
}