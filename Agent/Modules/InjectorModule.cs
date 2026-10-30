using Agent.App;
using Agent.Settings;
using Domain.Agents;
using Domain.Contracts;
using Domain.DTOs;
using Domain.DTOs.Channel;
using Domain.Judgments;
using Domain.Monitor;
using Domain.Outposts;
using Domain.Prompts;
using Domain.Security;
using Domain.Security.Signing;
using Domain.Skills;
using Domain.Tools.FileSystem;
using Infrastructure.Agents;
using Infrastructure.Agents.ChatClients;
using Infrastructure.Agents.Mcp;
using Infrastructure.Clients;
using Infrastructure.Clients.Channels;
using Infrastructure.Judgments;
using Infrastructure.Metrics;
using Infrastructure.StateManagers;
using Microsoft.Extensions.Hosting;
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
                HydrationDepthMessages = settings.Attachments.HydrationDepthMessages,
                MaxInlineImageBytes = settings.ReadImages.MaxInlineBytes
            };

            // Before anything is registered: a default naming an agent nobody configured routes
            // every unattributed message into an error, and the deployment should refuse to start
            // rather than discover it one message at a time.
            settings.AgentDefaults.Validate(settings.Agents.Select(a => a.Id));

            // And for a server the deployment configured without its credential: dialled bare, its
            // gate would refuse every call, and the session that needed it would fail far from here.
            var credentials = McpCredentials.From(settings.Mcp);
            var uncredentialed = credentials.MissingFor(settings.Agents.SelectMany(a => a.McpServerEndpoints)
                .Concat(settings.SubAgents.SelectMany(a => a.McpServerEndpoints))
                .Concat(settings.ChannelEndpoints.Select(c => c.Endpoint)));
            if (uncredentialed.Count > 0)
            {
                throw new InvalidOperationException(
                    $"No MCP credential for {string.Join(", ", uncredentialed)}: set MCP__SECRETS__<host> for each " +
                    "configured server, the same value the server reads as MCP__SHAREDSECRET.");
            }

            // The same rule for the other thing an agent names rather than carries: a section id
            // nothing declares is a behaviour somebody meant to give an agent and it would
            // otherwise go missing in silence. The projection refuses it too, per agent built;
            // this refuses the deployment.
            PromptManifest.Validate(
                settings.Agents.Select(a => (a.Id, (IEnumerable<string>)a.PromptSections))
                    .Concat(settings.SubAgents.Select(a => (a.Id, (IEnumerable<string>)a.PromptSections))));

            services.Configure<AgentRegistryOptions>(options => options.Agents = settings.Agents);

            var lemonadeChatHost = new LemonadeChatHostOptions
            {
                ApiUrl = settings.LemonadeChat.ApiUrl,
                ApiKey = settings.LemonadeChat.ApiKey
            };

            return services
                .AddSingleton(settings.AgentDefaults)
                .AddSingleton(settings.Retention)
                // The credentials checked above, and the only ones: the agent factory applies them
                // to every configured endpoint it composes, and each channel connection presents
                // its own server's.
                .AddSingleton(credentials)
                // The agent's own key, opened before anything dials: a deployment generates it into
                // its volume on first start, and its public half is what every server verifies a call
                // token against (AgentKeysApi).
                .AddSingleton(_ => SigningKeyStore.Open(settings.Signing))
                // On the wall clock, which is what every server's gate checks a token's dates
                // against — not the TimeProvider registered below, which an eval pins to its
                // scenario's day for what the prompt says the date is.
                .AddSingleton(sp => new CallTokens(
                    sp.GetRequiredService<SigningKeyStore>(), TimeProvider.System, settings.CallTokens,
                    settings.Delegations))
                // A fire's delegation is the agent's own word, checked against its own keys — every
                // one it still publishes, so a rotation strands nothing before redelegate re-signs it —
                // on the wall clock it was signed on, and against the delivery default the servers
                // that raise fires resolve a deliverTo with (Domain/delivery.json).
                .AddSingleton(sp => new DelegationVerifier(
                    sp.GetRequiredService<SigningKeyStore>(), TimeProvider.System, settings.Delivery,
                    settings.Mcp.TokenVerification))
                .AddRedis(settings.Redis, settings.Retention)
                .AddMetricsPublishing("agent")
                .AddSingleton<ChatThreadResolver>()
                // One per host: the exec tool mints a call token on it for each sandbox command and
                // the bridge endpoint answers that command's file operations from it.
                .AddSingleton<IVfsBridge>(sp => new VfsBridge(sp.GetRequiredService<TimeProvider>(), settings.VfsBridge))
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
                // Registered as a plain IHostedService singleton, not through AddHostedService:
                // that one dedups by implementation type, so a second keep-alive of this class
                // would be silently dropped. Jev is asked on this connection too.
                .AddSingleton<IHostedService, HostedConnectionKeepAlive>(sp => new HostedConnectionKeepAlive(
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
                .AddHostedService<ModelCapabilityRefresher>()
                .AddLemonadeChatHost(lemonadeChatHost)
                .AddTypeSafe(settings.TypeSafe.KeyedBy(settings.OpenRouter.ApiKey), settings.SkillPreload, settings.ExecScreen)
                .AddOutposts(settings.Outposts);
        }

        // The judge is registered as every Jev host registers it; the preloader and the exec screen
        // are this host's uses. With no key the judge answers absence, so the screen still runs:
        // every sandbox exec runs and every outpost exec is asked, which is the no-verdict rule.
        private IServiceCollection AddTypeSafe(
            TypeSafeOptions typeSafe, SkillPreloadSettings skillPreload, ExecScreenSettings execScreen) =>
            services
                .AddTypeSafeJudge(typeSafe, keepConnectionAlive: false)
                .AddSingleton<ISkillPreloader>(sp => new SkillPreloader(
                    sp.GetRequiredService<IJudge>(),
                    skillPreload,
                    sp.GetRequiredService<TimeProvider>(),
                    sp.GetRequiredService<IMetricsPublisher>()))
                .AddSingleton<IExecScreen>(sp => new ExecScreen(
                    sp.GetRequiredService<IJudge>(),
                    execScreen,
                    sp.GetRequiredService<TimeProvider>(),
                    sp.GetRequiredService<IMetricsPublisher>()));

        // An empty address is the feature switched off: no discovery, no refresher, and an empty
        // source for the catalogue to read. A configured one is asked for its models on a timer.
        private IServiceCollection AddLemonadeChatHost(LemonadeChatHostOptions host)
        {
            if (!host.IsConfigured)
            {
                return services.AddSingleton<ILemonadeModelSource>(FixedLemonadeModelSource.None);
            }

            return services
                .AddSingleton(host)
                // Shares the chat clients' pool: the pool is per host, so the box gets its own
                // connections inside it, and the discovery rides the same ones the turns use.
                .AddSingleton(sp => new LemonadeModelDiscovery(
                    new HttpClient(HostedConnectionPool.Shared, disposeHandler: false),
                    host,
                    sp.GetRequiredService<ILogger<LemonadeModelDiscovery>>()))
                .AddSingleton<ILemonadeModelSource>(sp => sp.GetRequiredService<LemonadeModelDiscovery>())
                .AddHostedService<LemonadeModelRefresher>();
        }

        public IServiceCollection AddChatMonitoring(AgentSettings settings, CommandLineParams cmdParams)
        {
            foreach (var endpoint in settings.ChannelEndpoints)
            {
                var channelId = endpoint.ChannelId;
                var attachOnly = endpoint.AttachOnly;
                var hostsReviewCopies = endpoint.HostsReviewCopies;
                // A channel server's /mcp is gated like every other deployment server's, so each
                // connection — and every reconnect it makes — presents that server's credential.
                services = services.AddSingleton<IChannelConnection>(sp =>
                    new McpChannelConnection(
                        channelId, attachOnly, sp.GetService<ILogger<McpChannelConnection>>(),
                        mcpSecret: sp.GetRequiredService<McpCredentials>().For(endpoint.Endpoint),
                        callTokens: sp.GetRequiredService<CallTokens>())
                    {
                        HostsReviewCopies = hostsReviewCopies
                    });
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
                            sp.GetRequiredService<IModelCapabilityCatalog>(),
                            sp.GetRequiredService<ILemonadeModelSource>()),
                        sp.GetRequiredService<ILogger<ChannelConnectionHost>>(),
                        sp.GetService<IHostApplicationLifetime>()?.ApplicationStarted ?? default));
        }

        private IServiceCollection AddOutposts(OutpostConfiguration outposts) =>
            // The registry and the secret travel together, because neither is any use without the
            // other: a registration nobody may dial is a mount that cannot be reached.
            services.AddSingleton(sp => new OutpostAccess(
                sp.GetRequiredService<IOutpostRegistry>(), outposts.SharedSecret));

        private IServiceCollection AddRedis(RedisConfiguration config, RetentionSettings retention)
        {
            return services
                .AddSingleton(TimeProvider.System)
                .AddSingleton<IConnectionMultiplexer>(_ => ConnectionMultiplexer.Connect(config.ConnectionString))
                .AddSingleton<IThreadStateStore>(sp => new RedisThreadStateStore(
                    sp.GetRequiredService<IConnectionMultiplexer>(),
                    retention,
                    sp.GetRequiredService<TimeProvider>()))
                .AddSingleton<IPushSubscriptionStore>(sp => new RedisPushSubscriptionStore(
                    sp.GetRequiredService<IConnectionMultiplexer>()))
                // Outside this process for the same reason an outpost registration is: recycling the
                // agent must not blind it to a picture that is still in view.
                .AddSingleton<IReadImageStore>(sp => new RedisReadImageStore(
                    sp.GetRequiredService<IConnectionMultiplexer>()))
                // An outpost registration survives the agent recycling because it lives here
                // rather than in this process: a container restart must not silently drop every
                // machine that had announced itself.
                .AddSingleton<IOutpostStore>(sp => new RedisOutpostStore(
                    sp.GetRequiredService<IConnectionMultiplexer>()))
                .AddSingleton<IOutpostRegistry>(sp => new OutpostRegistry(
                    sp.GetRequiredService<IOutpostStore>(),
                    sp.GetRequiredService<IMetricsPublisher>(),
                    sp.GetRequiredService<TimeProvider>()));
        }
    }
}