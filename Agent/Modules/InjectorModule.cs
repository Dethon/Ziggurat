using Agent.App;
using Agent.Settings;
using Domain.Agents;
using Domain.Contracts;
using Domain.DTOs;
using Domain.DTOs.Channel;
using Domain.Monitor;
using Infrastructure.Agents;
using Infrastructure.Agents.ChatClients;
using Infrastructure.Clients;
using Infrastructure.Clients.Channels;
using Infrastructure.Metrics;
using Infrastructure.StateManagers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace Agent.Modules;

public static class InjectorModule
{
    extension(IServiceCollection services)
    {
        public IServiceCollection AddAgent(AgentSettings settings)
        {
            var llmConfig = new OpenRouterConfig
            {
                ApiUrl = settings.OpenRouter.ApiUrl,
                ApiKey = settings.OpenRouter.ApiKey,
                MaxContextTokens = settings.OpenRouter.MaxContextTokens,
                ProviderRouting = settings.OpenRouter.ProviderRouting,
                PatchableModelIds = settings.PatchableModels.Select(m => m.Id).ToList(),
                HydrationDepthMessages = settings.Attachments.HydrationDepthMessages
            };

            services.Configure<AgentRegistryOptions>(options => options.Agents = settings.Agents);

            return services
                .AddSingleton(settings.Retention)
                .AddRedis(settings.Redis, settings.Retention)
                .AddMetricsPublishing("agent")
                .AddSingleton<ChatThreadResolver>()
                .AddSingleton<IDomainToolRegistry, DomainToolRegistry>()
                .AddSingleton<CustomAgentRegistry>()
                .AddSingleton<IAgentDefinitionProvider, AgentDefinitionProvider>()
                .AddSingleton<IAgentFactory>(sp =>
                    new MultiAgentFactory(
                        sp,
                        sp.GetRequiredService<IAgentDefinitionProvider>(),
                        llmConfig,
                        sp.GetRequiredService<IDomainToolRegistry>(),
                        sp.GetRequiredService<IMetricsPublisher>(),
                        sp.GetService<ILoggerFactory>()))
                // Shares the chat clients' connection pool, which is the whole point: a
                // keep-alive against its own pool would warm a connection no turn ever uses.
                .AddHostedService(sp => new HostedConnectionKeepAlive(
                    new HttpClient(HostedConnectionPool.Shared, disposeHandler: false),
                    new HostedConnectionKeepAliveOptions
                    {
                        BaseAddress = settings.OpenRouter.ApiUrl,
                        ApiKey = settings.OpenRouter.ApiKey
                    },
                    sp.GetRequiredService<IMetricsPublisher>(),
                    TimeProvider.System,
                    sp.GetRequiredService<ILogger<HostedConnectionKeepAlive>>()))
                // Shares the chat clients' pool for the same reason the keep-alive does.
                .AddSingleton(sp => new OpenRouterModelCapabilities(
                    new HttpClient(HostedConnectionPool.Shared, disposeHandler: false)
                    {
                        BaseAddress = new Uri(settings.OpenRouter.ApiUrl)
                    },
                    sp.GetRequiredService<ILogger<OpenRouterModelCapabilities>>()))
                .AddSingleton<IModelCapabilityCatalog>(sp =>
                    sp.GetRequiredService<OpenRouterModelCapabilities>())
                .AddHostedService<ModelCapabilityRefresher>();
        }

        public IServiceCollection AddChatMonitoring(AgentSettings settings, CommandLineParams cmdParams)
        {
            foreach (var endpoint in settings.ChannelEndpoints)
            {
                var channelId = endpoint.ChannelId;
                var attachOnly = endpoint.AttachOnly;
                services = services.AddSingleton<IChannelConnection>(sp =>
                    new McpChannelConnection(channelId, attachOnly, sp.GetService<ILogger<McpChannelConnection>>()));
            }

            return services
                .AddSingleton<IReadOnlyList<IChannelConnection>>(sp =>
                    sp.GetServices<IChannelConnection>().ToList())
                .AddSingleton<IAttachmentSource>(sp => new ChannelAttachmentSource(
                    sp.GetRequiredService<IReadOnlyList<IChannelConnection>>(),
                    sp.GetService<ILogger<ChannelAttachmentSource>>()))
                .AddSingleton<ChatMonitor>()
                .AddHostedService<ChatMonitoring>()
                .AddHostedService(sp =>
                    new ChannelConnectionHost(
                        settings.ChannelEndpoints,
                        sp.GetServices<IChannelConnection>().OfType<IMcpChannelConnection>().ToList(),
                        // Read per registration, not captured once: attachment capability is
                        // discovered from the model provider and refreshed while the agent runs.
                        () => AgentCatalogBuilder.Build(
                            settings.Agents,
                            settings.PatchableModels,
                            sp.GetRequiredService<IModelCapabilityCatalog>()),
                        sp.GetRequiredService<ILogger<ChannelConnectionHost>>()));
        }

        private IServiceCollection AddRedis(RedisConfiguration config, RetentionSettings retention)
        {
            return services
                .AddSingleton(TimeProvider.System)
                .AddSingleton<IConnectionMultiplexer>(_ => ConnectionMultiplexer.Connect(config.ConnectionString))
                .AddSingleton<IThreadStateStore>(sp => new RedisThreadStateStore(
                    sp.GetRequiredService<IConnectionMultiplexer>(),
                    retention.PurgeHorizon,
                    sp.GetRequiredService<TimeProvider>()))
                .AddSingleton<IPushSubscriptionStore>(sp => new RedisPushSubscriptionStore(
                    sp.GetRequiredService<IConnectionMultiplexer>()))
                .AddHostedService<TopicMigrationHost>();
        }
    }
}