using Domain.Contracts;
using Domain.Judgments;
using Domain.Prompts;
using Domain.Tools.Web;
using Infrastructure.Clients;
using Infrastructure.Clients.Browser;
using Infrastructure.Extensions;
using Infrastructure.Judgments;
using Infrastructure.Metrics;
using Infrastructure.StateManagers;
using Infrastructure.Utils;
using Mcp.Hosting;
using McpServerWebSearch.McpPrompts;
using McpServerWebSearch.McpTools;
using McpServerWebSearch.Settings;
using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;

namespace McpServerWebSearch.Modules;

public static class ConfigModule
{
    extension(IServiceCollection services)
    {
        public IServiceCollection ConfigureMcp(McpSettings settings)
        {
            services
                // A lazy factory, like the voice channel's: nothing dials Redis until the first
                // publish resolves the publisher, so registration runs with no container.
                .AddSingleton<IConnectionMultiplexer>(
                    _ => RedisConnection.ConnectResiliently(settings.RedisConnectionString))
                .AddMetricsPublishing("mcp-websearch")
                .AddTypeSafeJudge(settings.TypeSafe)
                .AddWebSearchClients(settings)
                .AddToolServer(settings, ToolResponse.Create)
                .WithTools<McpWebSearchTool>()
                .WithTools<McpWebBrowseTool>()
                .WithTools<McpWebSnapshotTool>()
                .WithTools<McpWebActionTool>()
                .WithTools<McpViewImageTool>()
                // The skill that teaches this server's tools ships beside them, so a deployment
                // without the browser cannot advertise how to drive one.
                .AddSkills(WebBrowsingSkill.Text)
                .WithPrompts<McpSystemPrompt>();

            return services;
        }

        private IServiceCollection AddWebSearchClients(McpSettings settings)
        {
            services.AddHttpClient<IWebSearchClient, BraveSearchClient>((httpClient, _) =>
                {
                    httpClient.BaseAddress = new Uri(settings.BraveSearch.ApiUrl);
                    httpClient.Timeout = TimeSpan.FromSeconds(30);
                    return new BraveSearchClient(httpClient, settings.BraveSearch.ApiKey);
                })
                .AddRetryOnRateLimitPolicy(attempts: 3, waitTime: TimeSpan.FromSeconds(2))
                .AddRetryWithExponentialWaitPolicy(
                    attempts: 3,
                    waitTime: TimeSpan.FromSeconds(1),
                    attemptTimeout: TimeSpan.FromSeconds(15));

            if (!string.IsNullOrEmpty(settings.CapSolver?.ApiKey))
            {
                services.AddHttpClient<ICaptchaSolver, CapSolverClient>((httpClient, _) =>
                {
                    httpClient.Timeout = TimeSpan.FromMinutes(3);
                    return new CapSolverClient(httpClient, settings.CapSolver.ApiKey);
                });
            }

            services.AddSingleton<IWebBrowser>(sp =>
            {
                var captchaSolver = sp.GetService<ICaptchaSolver>();
                return new PlaywrightWebBrowser(
                    captchaSolver,
                    settings.Camoufox?.WsEndpoint,
                    tabCap: settings.Browsing.TabCap,
                    idleTimeout: TimeSpan.FromMinutes(settings.Browsing.SessionIdleTimeoutMinutes),
                    // No key is the feature off, all the way off: a dismisser with no judge skips
                    // the control listing too, which is a chained-locator EvaluateAll over every
                    // overlay the cheap paths left standing on every navigation.
                    modalDismisser: new ModalDismisser(
                        settings.TypeSafe.IsConfigured
                            ? new ModalJudge(sp.GetRequiredService<IJudge>(), settings.Judgment, TimeProvider.System)
                            : null),
                    metricsPublisher: sp.GetRequiredService<IMetricsPublisher>());
            });

            return services;
        }
    }
}