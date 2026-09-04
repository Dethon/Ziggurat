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

            // The calendar's actions and the recorder's two reads are always served here: Home
            // Assistant's own catalog cannot list an event's uid, delete one, or read the recorder,
            // and the replacements need nothing beyond the HA token.
            IReadOnlyList<HaServiceDefinition> recorder = [HaHistoryActions.History, HaStatisticsActions.Statistics];
            IReadOnlyList<HaServiceDefinition> served = musicConfigured
                ? [.. HaCalendarActions.All, .. recorder, HaMusicActions.PodcastEpisodes]
                : [.. HaCalendarActions.All, .. recorder];

            services
                .AddHomeAssistantClient(settings.HomeAssistant.BaseUrl, settings.HomeAssistant.Token)
                .AddSingleton(sp => new HaCatalogProvider(
                    sp.GetRequiredService<IHomeAssistantClient>,
                    extraServices: served,
                    logger: sp.GetRequiredService<ILogger<HaCatalogProvider>>()))
                .AddSingleton(sp => new HaFileSystem(
                    sp.GetRequiredService<HaCatalogProvider>(),
                    sp.GetRequiredService<IHomeAssistantClient>,
                    musicClientFactory: musicConfigured ? sp.GetRequiredService<IMusicAssistantClient> : null))
                .AddSingleton(sp => new HomeAssistantSetupSummary(sp.GetRequiredService<HaCatalogProvider>()))
                .AddToolServer(settings, ToolResponse.Create)
                .AddFileSystemTools<HaFileSystem>()
                .AddFileSystemResource<HaFileSystem>()
                .WithPrompts<McpSystemPrompt>();

            return services;
        }
    }
}