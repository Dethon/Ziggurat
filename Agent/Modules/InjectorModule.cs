using Agent.App;
using Agent.Settings;
using Domain.Agents;
using Domain.Contracts;
using Domain.DTOs;
using Domain.DTOs.Channel;
using Domain.DTOs.Tunnel;
using Domain.Judgments;
using Domain.Monitor;
using Domain.Prompts;
using Domain.Security;
using Domain.Security.Signing;
using Domain.Skills;
using Domain.Tools.FileSystem;
using Infrastructure.Agents;
using Infrastructure.Agents.ChatClients;
using Infrastructure.Agents.Mcp;
using Infrastructure.Clients;
using Infrastructure.Clients.Accounts;
using Infrastructure.Clients.Channels;
using Infrastructure.Conversations;
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
            // Before any of them is read: a section the settings lack is named here, not met as a
            // NullReferenceException wherever it is first used.
            RequiredSettings.Checked(settings);

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

            // The agents are each household's (HouseholdAgents): what the deployment itself names is its
            // worker profiles, its shared servers and its channels, each checked here before anything is
            // registered. A template reaches only the shared servers among these (HouseholdAgents).
            var named = settings.SubAgents.SelectMany(a => a.McpServerEndpoints)
                .Concat(settings.SharedServers.All)
                .ToList();

            // An endpoint is a server's URL or a household server's name (reached through its tunnel),
            // and nothing else: anything else would slip past the credential check below and fail in
            // the first session that dialled it — or, for a channel, in a connection retrying for ever.
            var unreadable = named
                .Concat(settings.ChannelEndpoints.Select(c => c.Endpoint))
                .Where(endpoint => ServerAddress.FromUrl(endpoint) is null && !TunnelCredentialProtocol.IsServerName(endpoint))
                .Distinct(StringComparer.Ordinal)
                .ToList();
            if (unreadable.Count > 0)
            {
                throw new InvalidOperationException(
                    $"MCP endpoints that are neither an http(s) URL nor a household server's name: {string.Join(", ", unreadable)}.");
            }

            // And for a server the deployment configured without its credential: dialled bare, its
            // gate would refuse every call, and the session that needed it would fail far from here.
            var credentials = McpCredentials.From(settings.Mcp);
            var uncredentialed = credentials.MissingFor(named.Concat(settings.ChannelEndpoints.Select(c => c.Endpoint)));
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
                AgentTemplates.All.Select(a => (a.Id, (IEnumerable<string>)a.PromptSections))
                    .Concat(settings.SubAgents.Select(a => (a.Id, (IEnumerable<string>)a.PromptSections))));

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
                // Before the channel connections start, so a restarted agent has its people's
                // custom agents back by the first turn; a plain IHostedService singleton, so the
                // one instance the provider reads is the one that loads.
                .AddSingleton<ICustomAgentStore>(sp => new RedisCustomAgentStore(
                    sp.GetRequiredService<IConnectionMultiplexer>()))
                .AddSingleton(sp => new CustomAgentRegistry(sp.GetRequiredService<ICustomAgentStore>()))
                .AddSingleton<IHostedService>(sp => sp.GetRequiredService<CustomAgentRegistry>())
                // Each household's agents, from its configuration: read with the agent's own credential
                // there (CALLERS__agent, the one it holds for its own API) and a token it signs for
                // the configuration, so that credential alone, which SexyTime's server shares, opens
                // no household's key.
                // The signing key is opened at the first ask, not as the reader is made: the host
                // resolves its hosted services before anything is asked.
                .AddHouseholdConfiguration(
                    settings.Accounts, settings.AgentApi.SharedSecret,
                    sp => () => HouseholdConfigurationClient.AgentToken(sp.GetRequiredService<CallTokens>())())
                .AddSingleton(settings.SharedServers)
                .AddSingleton<IHouseholdAgentSource>(sp => new HouseholdAgentSource(
                    sp.GetRequiredService<IHouseholdConfiguration>(),
                    settings.SharedServers,
                    sp.GetService<ILogger<HouseholdAgentSource>>()))
                .AddSingleton<IAgentDefinitionProvider, AgentDefinitionProvider>()
                // Read once the agent listens, and waited for by the channel connections, so their
                // first registration carries the home's agents rather than none.
                .AddSingleton(sp => new HomeAgentCatalog(
                    sp.GetRequiredService<IHouseholdAgentSource>(),
                    settings.Accounts.ConfigurationLifetime,
                    sp.GetRequiredService<ILogger<HomeAgentCatalog>>(),
                    sp.GetService<IHostApplicationLifetime>()?.ApplicationStarted ?? default))
                .AddSingleton<IHostedService>(sp => sp.GetRequiredService<HomeAgentCatalog>())
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
                .AddTypeSafe(settings.TypeSafe.KeyedBy(settings.OpenRouter.ApiKey), settings.SkillPreload, settings.ExecScreen);
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
                // A channel server's /mcp is gated like every other deployment server's, so each run
                // of the connection — and every reconnect it makes — presents that server's
                // credential, which the host hands it with the endpoint (a household server's tunnel
                // presents its own).
                services = services.AddSingleton<IChannelConnection>(sp =>
                    new McpChannelConnection(
                        channelId, attachOnly, sp.GetService<ILogger<McpChannelConnection>>(),
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
                            sp.GetRequiredService<HomeAgentCatalog>().Current,
                            settings.PatchableModels,
                            sp.GetRequiredService<IModelCapabilityCatalog>(),
                            sp.GetRequiredService<ILemonadeModelSource>()),
                        sp.GetRequiredService<ILogger<ChannelConnectionHost>>(),
                        sp.GetService<IHostApplicationLifetime>()?.ApplicationStarted ?? default,
                        sp.GetRequiredService<McpCredentials>(),
                        // A channel endpoint naming a household server follows the home household's
                        // tunnel of that name (the relay's, AddTunnelRelay).
                        sp.GetService<IOwnChannelServers>(),
                        sp.GetRequiredService<HomeAgentCatalog>().FirstRead));
        }

        private IServiceCollection AddRedis(RedisConfiguration config, RetentionSettings retention)
        {
            return services
                .AddSingleton(TimeProvider.System)
                .AddSingleton<IConnectionMultiplexer>(_ => ConnectionMultiplexer.Connect(config.ConnectionString))
                .AddSingleton<IThreadStateStore>(sp => new RedisThreadStateStore(
                    sp.GetRequiredService<IConnectionMultiplexer>(),
                    retention,
                    sp.GetRequiredService<TimeProvider>()))
                // The topics the agent mints itself: a voice conversation's (TopicMintingApi), and a
                // fire's that only an attach-only channel delivers, which nothing else anchors.
                .AddSingleton<IConversationFactory>(sp => new ConversationFactory(
                    sp.GetRequiredService<IThreadStateStore>(), sp.GetRequiredService<TimeProvider>()))
                .AddSingleton<IPushSubscriptionStore>(sp => new RedisPushSubscriptionStore(
                    sp.GetRequiredService<IConnectionMultiplexer>()))
                // Outside this process: recycling the agent must not blind it to a picture that is
                // still in view.
                .AddSingleton<IReadImageStore>(sp => new RedisReadImageStore(
                    sp.GetRequiredService<IConnectionMultiplexer>()));
        }
    }
}