using Domain.Contracts;
using Domain.Prompts;
using Domain.Tools.HomeAssistant.Vfs;
using Infrastructure.Extensions;
using Infrastructure.Utils;
using Mcp.Hosting;
using McpServerHomeAssistant.McpPrompts;
using McpServerHomeAssistant.Settings;
using Microsoft.Extensions.DependencyInjection;

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

            // The calendar's actions are always served here: Home Assistant's own catalog cannot list
            // an event's uid or delete one, and the replacement needs nothing beyond the HA token.
            IReadOnlyList<HaServiceDefinition> served = musicConfigured
                ? [.. HaCalendarActions.All, HaMusicActions.PodcastEpisodes]
                : HaCalendarActions.All;

            services
                .AddHomeAssistantClient(settings.HomeAssistant.BaseUrl, settings.HomeAssistant.Token)
                .AddSingleton(sp => new HaCatalogProvider(
                    sp.GetRequiredService<IHomeAssistantClient>,
                    extraServices: served))
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