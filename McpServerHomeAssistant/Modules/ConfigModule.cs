using Domain.Contracts;
using Domain.Prompts;
using Domain.Tools.HomeAssistant.Vfs;
using Infrastructure.Extensions;
using Infrastructure.Utils;
using Mcp.Hosting;
using McpServerHomeAssistant.McpPrompts;
using McpServerHomeAssistant.Settings;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace McpServerHomeAssistant.Modules;

public static class ConfigModule
{
    // The actions the mount serves itself rather than forwarding to a catalog service. The
    // calendar's and the recorder's two reads are always there: Home Assistant's own catalog cannot
    // list an event's uid, delete one, or read the recorder, and the replacements need nothing
    // beyond the HA token. The podcast listing exists only where Music Assistant does. The eval's
    // fake mount builds from this same list, so it serves exactly what a deployment serves.
    public static IReadOnlyList<HaServiceDefinition> ServedActions(bool musicConfigured) =>
        musicConfigured
            ? [.. HaCalendarActions.All, HaHistoryActions.History, HaStatisticsActions.Statistics, HaMusicActions.PodcastEpisodes]
            : [.. HaCalendarActions.All, HaHistoryActions.History, HaStatisticsActions.Statistics];

    extension(IServiceCollection services)
    {
        public IServiceCollection ConfigureMcp(McpSettings settings)
        {
            // The podcast-episode action is advertised only when Music Assistant is reachable, so a
            // deployment without it never lists an action that cannot work.
            var music = settings.MusicAssistant;
            var musicConfigured = music?.IsConfigured == true;
            if (musicConfigured)
            {
                services.AddMusicAssistantClient(music!.BaseUrl, music.Token);
            }

            services
                .AddHomeAssistantClient(settings.HomeAssistant.BaseUrl, settings.HomeAssistant.Token)
                .AddSingleton(sp => new HaCatalogProvider(
                    sp.GetRequiredService<IHomeAssistantClient>,
                    extraServices: ServedActions(musicConfigured),
                    logger: sp.GetRequiredService<ILogger<HaCatalogProvider>>()))
                .AddSingleton(sp => new HaFileSystem(
                    sp.GetRequiredService<HaCatalogProvider>(),
                    sp.GetRequiredService<IHomeAssistantClient>,
                    musicClientFactory: musicConfigured ? sp.GetRequiredService<IMusicAssistantClient> : null))
                .AddSingleton(sp => new HaWatches(sp.GetRequiredService<IHomeAssistantClient>))
                .AddSingleton(sp => new HomeAssistantSetupSummary(
                    sp.GetRequiredService<HaCatalogProvider>(), sp.GetRequiredService<HaWatches>()))
                .AddSingleton(TimeProvider.System)
                .AddToolServer(settings, ToolResponse.Create)
                // Broadcast, so an agent that is merely reconnecting still receives a fire; the
                // callback answers Home Assistant 503 only when nobody is registered at all, and
                // buffers nothing itself (docs/adr/0038).
                .AddChannelServer(DeliveryPolicy.Broadcast, noOutboundSurface: true)
                .AddFileSystemTools<HaFileSystem>()
                .AddFileSystemResource<HaFileSystem>()
                .WithPrompts<McpSystemPrompt>();

            return services;
        }
    }
}