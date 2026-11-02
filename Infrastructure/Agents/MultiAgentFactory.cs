using Domain.Agents;
using Domain.Contracts;
using Domain.DTOs;
using Domain.DTOs.Channel;
using Domain.Extensions;
using Domain.Skills;
using Domain.Tools.FileSystem;
using Infrastructure.Agents.ChatClients;
using Infrastructure.Agents.Mcp;
using Infrastructure.Metrics;
using Infrastructure.StateManagers;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Infrastructure.Agents;

public sealed class MultiAgentFactory(
    // Nullable, truthfully: the sub-agent tests build the factory without a container, and every
    // service resolved from it is optional for that path — the one exception is the thread state
    // store, which only a history-keeping spec reaches and which refuses by name below.
    IServiceProvider? serviceProvider,
    IAgentDefinitionProvider definitionProvider,
    OpenRouterConfig openRouterConfig,
    IDomainToolRegistry domainToolRegistry,
    IMetricsPublisher? metricsPublisher = null,
    ILoggerFactory? loggerFactory = null) : IAgentFactory
{
    private readonly McpPromptCache _promptCache = new(TimeProvider.System, TimeSpan.FromSeconds(60));

    private readonly ILogger? _logger = loggerFactory?.CreateLogger<MultiAgentFactory>();

    // Absent in a host with no Lemonade chat host configured, and in every factory built without a
    // container: then the whitelist is the configured list and every turn goes to OpenRouter.
    private readonly ILemonadeModelSource _lemonadeModels = LemonadeModelsOf(serviceProvider);

    private readonly LemonadeChatHostOptions? _lemonadeHost =
        serviceProvider?.GetService<LemonadeChatHostOptions>();

    // The credential each configured endpoint presents, by its host, read once for every agent and
    // worker this factory builds. Absent in a host that registered none, which presents nothing and
    // whose gates refuse, so the gate rather than this decides what an unset credential means.
    private readonly McpCredentials _credentials =
        serviceProvider?.GetService<McpCredentials>() ?? McpCredentials.From(null);

    // One source for every agent this factory builds, read on each turn that carries a patch.
    private readonly IPatchableModelSource _patchableModels = new PatchableModelWhitelist(
        openRouterConfig.PatchableModelIds ?? [], LemonadeModelsOf(serviceProvider));

    private string? LemonadeHostAddress => _lemonadeHost is { IsConfigured: true } host ? host.Address : null;

    private static ILemonadeModelSource LemonadeModelsOf(IServiceProvider? serviceProvider) =>
        serviceProvider?.GetService<ILemonadeModelSource>() ?? FixedLemonadeModelSource.None;

    public async Task<DisposableAgent> CreateAsync(
        AgentKey agentKey, string userId, string? agentId, IToolApprovalHandler approvalHandler, Account? account = null,
        string? tenantId = null, CancellationToken ct = default)
    {
        // The household the turn is for runs its own agents (a turn acting for nobody runs the home's).
        // A person's custom agents are registered under their account — household and person — so a
        // turn finds them by the account it acts for, never by its sender, which may be the account's
        // display name or anything a channel reports. A turn acting for no account sees the
        // household's agents alone.
        var householdId = tenantId ?? account?.TenantId;
        var household = await definitionProvider.ForHouseholdAsync(householdId, ct);
        // Before anything is built, so a household that cannot pay is asked nothing: not its agents'
        // servers, and no model.
        _ = household.Payer ?? throw new HouseholdWithoutKeyException(household.HouseholdId ?? householdId ?? "");
        var agents = await definitionProvider.GetAllAsync(householdId, account, ct);

        // By id or not at all. Falling back to the first configured agent read routing off the
        // order of a display catalogue, so a request that named nobody was answered by whichever
        // assistant happened to be listed first. Who answers a message that names no agent is
        // decided before the message is grouped (AgentDefaults, then the household's default).
        var definition = string.IsNullOrEmpty(agentId)
            ? null
            : agents.FirstOrDefault(a => a.Id == agentId);

        _ = definition ?? throw new InvalidOperationException(
            string.IsNullOrEmpty(agentId)
                ? "No agent id was supplied and no default agent is configured for the channel."
                : $"No agent found for identifier '{agentId}'.");

        // Only the household's own agents' endpoints present a deployment credential: they are its
        // configuration's, which its owner cannot point anywhere, where a person's registered agent
        // names whatever its registration said.
        var deployment = new DeploymentEndpoints(household.Agents.SelectMany(a => a.McpServerEndpoints));
        var spec = AgentSpecProjection.ForAgent(
            definition, agentKey, userId, openRouterConfig, _patchableModels,
            LemonadeHostAddress, _credentials, deployment, _logger, householdId, household);

        return Build(spec, approvalHandler);
    }

    public DisposableAgent CreateSubAgent(
        SubAgentDefinition definition,
        IToolApprovalHandler approvalHandler,
        SpawnContext spawn)
    {
        var spec = AgentSpecProjection.ForSubAgent(
            definition, spawn, openRouterConfig, _patchableModels, LemonadeHostAddress, _credentials, _logger);

        return Build(spec, approvalHandler);
    }

    // Everything that differs between an agent and a subagent was resolved by the projection,
    // so nothing here asks which one it is building.
    private DisposableAgent Build(AgentSpec spec, IToolApprovalHandler approvalHandler)
    {
        IMetricsPublisher agentPublisher = metricsPublisher is not null
            ? new AgentMetricsPublisher(metricsPublisher, spec.AgentId, spec.MetricsAgentName, spec.TenantId)
            : NoOpMetricsPublisher.Instance;

        var chatClient = CreateChatClient(
            spec.Model, agentPublisher, spec.MaxContextTokens,
            sessionId: spec.RoutingSessionId,
            providerRouting: spec.ProviderRouting,
            key: spec.Key,
            keyHousehold: spec.TenantId,
            turnZone: TimeZones.Named(spec.TimeZone));

        // Composed from the spec this build is already running against, so the parent's values
        // reach the projection with no second source of truth for any of them.
        var spawn = new SpawnContext(
            spec.ConversationId, spec.UserId, spec.WhitelistPatterns, spec.UsesOutposts,
            spec.Model, spec.ProviderRouting, spec.TenantId, spec.WithheldServers, spec.TimeZone, spec.Key);

        // The spawner is absent in every deployment, so the factory builds its own workers; when
        // one is registered it stands in for them whole, which is the only way a scenario about
        // the parent's decision can avoid paying for a worker's answer.
        var spawner = serviceProvider?.GetService<ISubAgentSpawner>();

        var featureConfig = new FeatureConfig(
            SubAgentFactory: def => spawner is null
                ? CreateSubAgent(def, approvalHandler, spawn)
                : spawner.Spawn(def),
            UserId: spec.UserId,
            ConversationContextProvider: () => ConversationContextMeta.Current);

        var domainTools = domainToolRegistry
            .GetToolsForFeatures(spec.EnabledFeatures, featureConfig)
            .ToList();
        var domainPrompts = domainToolRegistry
            .GetPromptsForFeatures(spec.EnabledFeatures)
            .ToList();

        var stateStore = spec.KeepsHistory
            ? (serviceProvider ?? throw new InvalidOperationException(
                    "A history-keeping agent needs a service provider to resolve its thread state "
                    + "store, and this factory was built without one."))
                .GetRequiredService<IThreadStateStore>()
            : new NullThreadStateStore();

        var effectiveClient = new ToolApprovalChatClient(
            chatClient, approvalHandler, spec.ConversationId, spec.WhitelistPatterns, agentPublisher,
            // Absent in every deployment: only an evaluation harness registers one, and what it
            // watches has to be the agent the deployment builds rather than one assembled beside it.
            serviceProvider?.GetService<IToolInvocationObserver>(),
            // Optional as the preloader is: a host with no screen runs an unasked exec as it did
            // before the screen existed. A worker gets it by this same line, screened on its
            // delegation prompt.
            serviceProvider?.GetService<IExecScreen>(),
            spec.AgentId,
            spec.Payer);

        return new McpAgent(
            spec,
            effectiveClient,
            stateStore,
            agentPublisher,
            // The deployment registers the system clock; an evaluation harness registers the
            // instant its scenario is pinned to, so the prompt's date agrees with the turn's.
            serviceProvider?.GetService<TimeProvider>() ?? TimeProvider.System,
            domainTools,
            domainPrompts,
            loggerFactory,
            _promptCache,
            // Optional: a host with no outposts — every test host but the outposts' own — simply
            // has none to offer, which is the same answer as an agent that did not opt in.
            serviceProvider?.GetService<IOutposts>(),
            BuildReadImageSupport(spec),
            // Optional for the same reason: a host with no judge preloads nothing and the model
            // loads for itself. A worker gets it by this same line, judged on its delegation
            // prompt, so a delegated task also starts on the task.
            serviceProvider?.GetService<ISkillPreloader>(),
            // Optional as well: a host with no bridge endpoint runs sandbox commands with only the
            // sandbox's own disk, as before the bridge existed.
            serviceProvider?.GetService<IVfsBridge>(),
            // The agent's call tokens, which every server's gate asks a tool call for. The
            // deployment registers them; a host that does not sends calls every gated server
            // refuses, which is the loud failure an unsigned deployment should be. A worker gets
            // them by this same line, and signs for its parent's person because it carries its
            // parent's context.
            serviceProvider?.GetService<CallTokens>(),
            // Where a household's own server is reached: its tunnel in the deployment, the server a
            // stack hosts in process in a test. A host with neither reaches none, and a session that
            // needs one fails as a configured endpoint that will not answer does.
            serviceProvider?.GetService<IOwnServers>(),
            // Which own servers the turn's household set up is asked at each turn's start, of the
            // same view the agent was built from: a worker asks its parent's household by this same
            // line, so it reaches no server its parent's household never set up.
            ct => definitionProvider.ForHouseholdAsync(spec.TenantId, ct));
    }

    // Everything file_read needs to show the model a picture, resolved where the spec and the
    // container are both in reach. Each per-turn part stays a delegate: the tool set is built once
    // per session, and both the tool call being answered and the model the turn resolved to change
    // underneath it.
    private ReadImageSupport BuildReadImageSupport(AgentSpec spec) =>
        new()
        {
            // Optional exactly as the attachment source is: a host with no state store keeps reading
            // text and answers honestly that it cannot show an image.
            Store = serviceProvider?.GetService<IReadImageStore>(),
            CurrentCallId = () => FunctionInvokingChatClient.CurrentContext?.CallContent.CallId,
            // The id the send will look the bytes up under: hydration reads it off the turn's own
            // options, so the write reads it off the same turn rather than off the spec — a run
            // that carries no context (a harness, a benchmark) then refuses instead of storing
            // bytes under a key no send will ever ask for.
            CurrentConversationId = () => ConversationContextMeta.Current?.ConversationId,
            ModelAcceptsImages = () => AcceptsImages(
                FunctionInvokingChatClient.CurrentContext?.Options?.ModelId ?? spec.Model),
            MaxBytes = openRouterConfig.MaxInlineImageBytes
        };

    private bool AcceptsImages(string model) => Accepts(model, AttachmentKind.Image);

    private bool AcceptsVideo(string model) => Accepts(model, AttachmentKind.Video);

    // Permissive wherever the catalogue is silent, the same rule AttachmentCapability states: model
    // capability is discovered from the provider, and a lookup that has not answered yet must not
    // remove the feature from everyone. A Lemonade model is answered by the host that has it.
    private bool Accepts(string model, AttachmentKind kind)
    {
        if (LemonadeModelId.IsLemonade(model))
        {
            return LemonadeModelOf(model)?.AcceptedAttachmentKinds.Contains(kind) ?? true;
        }

        return serviceProvider?.GetService<IModelCapabilityCatalog>() is not { } catalog
               || catalog.GetAcceptedAttachmentKinds(model).Contains(kind);
    }

    // The smaller of the agent's window and the one the box loaded the model with; a model whose
    // window the box did not report fits the agent's, as an OpenRouter turn does.
    private int? LemonadeWindow(string model, int? agentWindow) =>
        (agentWindow, LemonadeModelOf(model)?.ContextWindow) switch
        {
            ({ } agent, { } discovered) => Math.Min(agent, discovered),
            (var agent, null) => agent,
            (null, var discovered) => discovered
        };

    private LemonadeModel? LemonadeModelOf(string model) =>
        _lemonadeModels.Current.FirstOrDefault(m =>
            string.Equals(LemonadeModelId.Namespaced(m.Id), model, StringComparison.OrdinalIgnoreCase));

    // The OpenRouter client is built exactly as before. With a Lemonade chat host configured it is
    // one side of a routing client, the other being a client for the box; without one it is the
    // whole client, and no turn can reach a Lemonade model because none is ever whitelisted.
    // It is sent with the household's own key, or the deployment's where none is given (the home's
    // agents and anything built for nobody); the box is the deployment's, on its own key.
    internal IChatClient CreateChatClient(
        string model, IMetricsPublisher? publisher = null, int? maxContextTokens = null,
        string? sessionId = null, ProviderRouting? providerRouting = null,
        HttpMessageHandler? transportHandler = null, HttpMessageHandler? lemonadeTransportHandler = null,
        ModelKey? key = null, string? keyHousehold = null, TimeZoneInfo? turnZone = null)
    {
        var effectivePublisher = publisher ?? metricsPublisher;
        var effectiveContext = maxContextTokens ?? openRouterConfig.MaxContextTokens;
        // Optional in both senses: a host with no channel connections registers none, and a
        // client built without a container has no way to ask. Neither is an error — the turn
        // simply reaches the model with no bytes put back.
        var attachmentSource = serviceProvider?.GetService<IAttachmentSource>();
        // The other half of the same pass: the tool writes the bytes here and this send reads
        // them back, so both ends have to be handed the one store or neither works. The
        // capability lookups ride along, because the send must not put an image or a video part
        // in front of a patched model that cannot take it — the wire rejects the whole request.
        var readImageStore = serviceProvider?.GetService<IReadImageStore>();

        IChatClient openRouter = new OpenRouterChatClient(
            openRouterConfig.ApiUrl,
            key?.Reveal() ?? openRouterConfig.ApiKey,
            model,
            effectiveContext,
            effectivePublisher,
            sessionId,
            providerRouting: providerRouting,
            transportHandler: transportHandler,
            attachmentSource: attachmentSource,
            hydrationDepthMessages: openRouterConfig.HydrationDepthMessages,
            readImageStore: readImageStore,
            modelAcceptsImages: AcceptsImages,
            modelAcceptsVideo: AcceptsVideo,
            turnZone: turnZone);

        // A household's own key that OpenRouter refuses is the owner's to fix, so their keys page is
        // told; the deployment's key is the operator's, and the box has a key of its own.
        if (key is not null && keyHousehold is not null && serviceProvider?.GetService<IHouseholdKeyReports>() is { } reports)
        {
            openRouter = new RefusedKeyReportingChatClient(
                openRouter, keyHousehold, reports, loggerFactory?.CreateLogger<RefusedKeyReportingChatClient>());
        }

        if (_lemonadeHost is not { IsConfigured: true } host)
        {
            return openRouter;
        }

        var lemonade = new LemonadeChatClient(
            host,
            effectivePublisher,
            sessionId,
            transportHandler: lemonadeTransportHandler,
            attachmentSource: attachmentSource,
            hydrationDepthMessages: openRouterConfig.HydrationDepthMessages,
            readImageStore: readImageStore,
            modelAcceptsImages: AcceptsImages,
            contextWindowFor: model => LemonadeWindow(model, effectiveContext),
            modelAcceptsVideo: AcceptsVideo,
            turnZone: turnZone);

        return new HostRoutingChatClient(openRouter, lemonade);
    }
}

public record OpenRouterConfig
{
    public required string ApiUrl { get; init; }
    public required string ApiKey { get; init; }
    public int? MaxContextTokens { get; init; }
    public ProviderRouting? ProviderRouting { get; init; }
    public IReadOnlyList<string>? PatchableModelIds { get; init; }
    public int HydrationDepthMessages { get; init; } = AttachmentHydration.DefaultDepthMessages;
    public long MaxInlineImageBytes { get; init; } = ReadImageSupport.DefaultMaxBytes;
}