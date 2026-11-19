using Domain.Contracts;
using Domain.Prompts;
using Domain.Tools.Timers.Vfs;
using Infrastructure.Clients.Accounts;
using Infrastructure.Clients.Voice;
using Infrastructure.Timers;
using Infrastructure.Utils;
using Mcp.Hosting;
using McpServerTimers.McpPrompts;
using McpServerTimers.Services;
using McpServerTimers.Settings;
using Microsoft.Extensions.DependencyInjection;

namespace McpServerTimers.Modules;

public static class ConfigModule
{
    public static IServiceCollection ConfigureTimers(this IServiceCollection services, TimerSettings settings)
    {
        services
            .AddSingleton(TimeProvider.System)
            .AddSingleton<ITimerStore, InMemoryTimerStore>()
            // The three hub-local capabilities reached over HTTP — fire (announce), stop (dismiss),
            // and target resolution/roster (satellites) — bound per household, so each request names
            // the household it is for.
            .AddHouseholdVoices(settings.VoiceHub.BaseUrl, settings.Mcp.Audience, settings.Mcp.SharedSecret, settings.Announce.Token)
            .AddSingleton(sp => new TimerFileSystem(
                sp.GetRequiredService<ITimerStore>(),
                sp.GetRequiredService<TimeProvider>(),
                sp.GetRequiredService<IHouseholdVoices>(),
                sp.GetRequiredService<IChannelAlerts>()))
            .AddHostedService<TimerFireService>()
            // Each household's settings, for the zone a timer's firing time is shown in: asked with
            // this server's own credential on the call's token. No Redis here, so a saved zone reaches
            // it when its kept answer runs out.
            .AddHouseholdConfiguration(settings.Accounts, settings.Mcp.SharedSecret);

        services
            .AddToolServer(settings, ToolResponse.Create)
            // Dual-role: a timer with no satellite to ring on rings on its owner's channels, which only
            // the agent holds, so this server raises it in its inbox as scheduling raises a fire.
            // Gate-on-live, as scheduling's: the timer is claimed once and fires once, so an alert
            // nobody was polling to take is reported not rung rather than kept to ring late.
            .AddChannelServer(DeliveryPolicy.GateOnLive, noOutboundSurface: true)
            .AddChannelAlerts()
            .AddFileSystemTools<TimerFileSystem>()
            .AddFileSystemResource<TimerFileSystem>()
            // The skill that teaches this server's files ships beside them.
            .AddSkills(CountdownTimersSkill.Text)
            .WithPrompts<TimersSystemPrompt>();

        return services;
    }
}