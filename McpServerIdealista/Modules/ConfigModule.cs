using Infrastructure.Clients;
using Infrastructure.Clients.Accounts;
using Infrastructure.Extensions;
using Infrastructure.Utils;
using Mcp.Hosting;
using McpServerIdealista.McpPrompts;
using McpServerIdealista.McpTools;
using McpServerIdealista.Settings;
using Microsoft.Extensions.DependencyInjection;

namespace McpServerIdealista.Modules;

public static class ConfigModule
{
    extension(IServiceCollection services)
    {
        public IServiceCollection ConfigureMcp(McpSettings settings)
        {
            services
                .AddIdealistaClients(settings)
                // The calling household's keys, asked with this server's own credential. It has no
                // Redis to hear a save on, so a saved key reaches it when what it kept runs out.
                .AddHouseholdConfiguration(settings.Accounts, settings.Mcp.SharedSecret)
                .AddToolServer(settings, ToolResponse.Create)
                .WithTools<McpPropertySearchTool>()
                .WithPrompts<McpSystemPrompt>();

            return services;
        }

        private IServiceCollection AddIdealistaClients(McpSettings settings)
        {
            services.AddHttpClient(IdealistaClients.ClientName, httpClient =>
                {
                    httpClient.BaseAddress = new Uri(settings.Idealista.ApiUrl);
                    httpClient.Timeout = TimeSpan.FromSeconds(30);
                })
                .AddRetryWithExponentialWaitPolicy(
                    attempts: 2,
                    waitTime: TimeSpan.FromSeconds(1),
                    attemptTimeout: TimeSpan.FromSeconds(15));

            return services.AddSingleton<IdealistaClients>();
        }
    }
}