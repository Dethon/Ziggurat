using Domain.Agents;
using Domain.Contracts;
using Domain.DTOs;
using Domain.DTOs.Channel;
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

    // One source for every agent this factory builds, read on each turn that carries a patch.
    private readonly IPatchableModelSource _patchableModels =
        new FixedPatchableModelSource(openRouterConfig.PatchableModelIds ?? []);

    public DisposableAgent Create(AgentKey agentKey, string userId, string? agentId, IToolApprovalHandler approvalHandler)
    {
        var agents = definitionProvider.GetAll(userId);

        // By id or not at all. Falling back to the first configured agent read routing off the
        // order of a display catalogue, so a request that named nobody was answered by whichever
        // assistant happened to be listed first. Who answers a message that names no agent is
        // decided from configuration before the message is grouped (AgentDefaults).
        var definition = string.IsNullOrEmpty(agentId)
            ? null
            : agents.FirstOrDefault(a => a.Id == agentId);

        _ = definition ?? throw new InvalidOperationException(
            string.IsNullOrEmpty(agentId)
                ? "No agent id was supplied and no default agent is configured for the channel."
                : $"No agent found for identifier '{agentId}'.");

        return CreateFromDefinition(agentKey, userId, definition, approvalHandler);
    }

    public DisposableAgent CreateSubAgent(
        SubAgentDefinition definition,
        IToolApprovalHandler approvalHandler,
        SpawnContext spawn)
    {
        var spec = AgentSpecProjection.ForSubAgent(definition, spawn, openRouterConfig, _logger);

        return Build(spec, approvalHandler);
    }

    private DisposableAgent CreateFromDefinition(
        AgentKey agentKey, string userId, AgentDefinition definition, IToolApprovalHandler approvalHandler)
    {
        var spec = AgentSpecProjection.ForAgent(
            definition, agentKey, userId, openRouterConfig, _patchableModels, _logger);

        return Build(spec, approvalHandler);
    }

    // Everything that differs between an agent and a subagent was resolved by the projection,
    // so nothing here asks which one it is building.
    private DisposableAgent Build(AgentSpec spec, IToolApprovalHandler approvalHandler)
    {
        IMetricsPublisher agentPublisher = metricsPublisher is not null
            ? new AgentMetricsPublisher(metricsPublisher, spec.MetricsAgentId)
            : NoOpMetricsPublisher.Instance;

        var chatClient = CreateChatClient(
            spec.Model, agentPublisher, spec.MaxContextTokens,
            sessionId: spec.RoutingSessionId,
            providerRouting: spec.ProviderRouting);

        // Composed from the spec this build is already running against, so the parent's values
        // reach the projection with no second source of truth for any of them.
        var spawn = new SpawnContext(
            spec.ConversationId, spec.UserId, spec.WhitelistPatterns, spec.UsesOutposts);

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
            serviceProvider?.GetService<IToolInvocationObserver>());

        return new McpAgent(
            spec,
            effectiveClient,
            stateStore,
            agentPublisher,
            TimeProvider.System,
            domainTools,
            domainPrompts,
            loggerFactory,
            _promptCache,
            // Optional: a host with no outpost access — every test host, and any deployment that
            // never turned them on — simply has no outposts to offer, which is the same answer as
            // an agent that did not opt in.
            serviceProvider?.GetService<OutpostAccess>(),
            BuildReadImageSupport(spec));
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

    // Permissive wherever the catalogue is silent, the same rule AttachmentCapability states: model
    // capability is discovered from the provider, and a lookup that has not answered yet must not
    // remove the feature from everyone.
    private bool AcceptsImages(string model) =>
        serviceProvider?.GetService<IModelCapabilityCatalog>() is not { } catalog
        || catalog.GetAcceptedAttachmentKinds(model).Contains(AttachmentKind.Image);

    internal IChatClient CreateChatClient(
        string model, IMetricsPublisher? publisher = null, int? maxContextTokens = null,
        string? sessionId = null, ProviderRouting? providerRouting = null,
        HttpMessageHandler? transportHandler = null)
    {
        var effectivePublisher = publisher ?? metricsPublisher;
        var effectiveContext = maxContextTokens ?? openRouterConfig.MaxContextTokens;

        return new OpenRouterChatClient(
            openRouterConfig.ApiUrl,
            openRouterConfig.ApiKey,
            model,
            effectiveContext,
            effectivePublisher,
            sessionId,
            providerRouting: providerRouting,
            transportHandler: transportHandler,
            // Optional in both senses: a host with no channel connections registers none, and a
            // client built without a container has no way to ask. Neither is an error — the turn
            // simply reaches the model with no bytes put back.
            attachmentSource: serviceProvider?.GetService<IAttachmentSource>(),
            hydrationDepthMessages: openRouterConfig.HydrationDepthMessages,
            // The other half of the same pass: the tool writes the bytes here and this send reads
            // them back, so both ends have to be handed the one store or neither works. The
            // capability lookup rides along, because the send must not put an image part in front
            // of a patched model that cannot take it — the wire rejects the whole request.
            readImageStore: serviceProvider?.GetService<IReadImageStore>(),
            modelAcceptsImages: AcceptsImages);
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

public sealed class AgentRegistryOptions
{
    public AgentDefinition[] Agents { get; set; } = [];
}