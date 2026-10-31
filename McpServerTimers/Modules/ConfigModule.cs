using Domain.Contracts;
using Domain.DTOs.Tunnel;
using Domain.Prompts;
using Domain.Tools.Timers.Vfs;
using Infrastructure.Clients.Relay;
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
        var hub = new Uri(settings.VoiceHub.BaseUrl);
        var client = services.AddHttpClient(VoiceHubHttp.ClientName, client =>
        {
            client.BaseAddress = hub;
            // Bound the wait: the hub returns 202 for announce (ringing is async) and resolves/dismisses
            // instantly, so a slow response means the hub is unhealthy. Fail fast rather than block the
            // once-per-second fire loop (or an agent's fs_create) on the default 100s timeout.
            client.Timeout = TimeSpan.FromSeconds(15);
        });
        // A household's voice server is reached through the relay's endpoint on the agent, never at
        // an address, and the relay asks who is calling: this server's name and its own credential.
        // A hub dialled directly is presented neither — it never asks for them.
        if (RelayedCallProtocol.IsRelayed(hub))
        {
            client.AddHttpMessageHandler(() => new RelayedCallCredential(settings.Mcp.Audience, settings.Mcp.SharedSecret));
        }

        services
            .AddSingleton(TimeProvider.System)
            .AddSingleton<ITimerStore, InMemoryTimerStore>()
            // The three hub-local capabilities reached over HTTP: fire (announce), stop (dismiss),
            // and target resolution/roster (satellites). The adapters create the named client per
            // call, so the factory's handler rotation works despite their singleton lifetimes.
            .AddSingleton<IInsistentAnnouncer>(sp => new HttpInsistentAnnouncer(sp.GetRequiredService<IHttpClientFactory>(), settings.Announce.Token))
            .AddSingleton<IAlertDismisser>(sp => new HttpAlertDismisser(sp.GetRequiredService<IHttpClientFactory>(), settings.Announce.Token))
            .AddSingleton<ISatelliteCatalog>(sp => new HttpSatelliteCatalog(sp.GetRequiredService<IHttpClientFactory>(), settings.Announce.Token))
            .AddSingleton(sp => new TimerFileSystem(
                sp.GetRequiredService<ITimerStore>(),
                sp.GetRequiredService<TimeProvider>(),
                sp.GetRequiredService<IAlertDismisser>(),
                sp.GetRequiredService<ISatelliteCatalog>()))
            .AddHostedService<TimerFireService>();

        services
            .AddToolServer(settings, ToolResponse.Create)
            .AddFileSystemTools<TimerFileSystem>()
            .AddFileSystemResource<TimerFileSystem>()
            // The skill that teaches this server's files ships beside them.
            .AddSkills(CountdownTimersSkill.Text)
            .WithPrompts<TimersSystemPrompt>();

        return services;
    }
}