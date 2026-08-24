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

            services
                .AddSingleton(TimeProvider.System)
                .AddHomeAssistantClient(settings.HomeAssistant.BaseUrl, settings.HomeAssistant.Token)
                .AddSingleton(sp => new HaCatalogProvider(
                    sp.GetRequiredService<IHomeAssistantClient>,
                    extraServices: musicConfigured ? [HaMusicActions.PodcastEpisodes] : null))
                .AddSingleton(sp => new HaFileSystem(
                    sp.GetRequiredService<HaCatalogProvider>(),
                    sp.GetRequiredService<IHomeAssistantClient>,
                    musicClientFactory: musicConfigured ? sp.GetRequiredService<IMusicAssistantClient> : null,
                    timeProvider: sp.GetRequiredService<TimeProvider>()))
                .AddSingleton(sp => new HomeAssistantSetupSummary(sp.GetRequiredService<HaCatalogProvider>()))
                .AddToolServer(settings, ToolResponse.Create)
                .AddFileSystemTools<HaFileSystem>()
                .AddFileSystemResource<HaFileSystem>()
                .WithPrompts<McpSystemPrompt>();

            return services;
        }
    }
}