using Domain.Agents;
using Domain.Contracts;
using Domain.DTOs;
using Infrastructure.Agents.ChatClients;
using Infrastructure.Agents.Mcp;
using Infrastructure.Metrics;
using Infrastructure.StateManagers;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Infrastructure.Agents;

public sealed class MultiAgentFactory(
    IServiceProvider serviceProvider,
    IAgentDefinitionProvider definitionProvider,
    OpenRouterConfig openRouterConfig,
    IDomainToolRegistry domainToolRegistry,
    IMetricsPublisher? metricsPublisher = null,
    ILoggerFactory? loggerFactory = null) : IAgentFactory
{
    private readonly McpPromptCache _promptCache = new(TimeProvider.System, TimeSpan.FromSeconds(60));

    private readonly ILogger? _logger = loggerFactory?.CreateLogger<MultiAgentFactory>();

    public DisposableAgent Create(AgentKey agentKey, string userId, string? agentId, IToolApprovalHandler approvalHandler)
    {
        var agents = definitionProvider.GetAll(userId);

        var definition = string.IsNullOrEmpty(agentId)
            ? agents.FirstOrDefault()
            : agents.FirstOrDefault(a => a.Id == agentId);

        _ = definition ?? throw new InvalidOperationException(
            string.IsNullOrEmpty(agentId)
                ? "No agents configured."
                : $"No agent found for identifier '{agentId}'.");

        return CreateFromDefinition(agentKey, userId, definition, approvalHandler);
    }

    public DisposableAgent CreateSubAgent(
        SubAgentDefinition definition,
        IToolApprovalHandler approvalHandler,
        string conversationId,
        string[] whitelistPatterns,
        string userId)
    {
        var spec = AgentSpecProjection.ForSubAgent(
            definition, conversationId, whitelistPatterns, userId, openRouterConfig, _logger);

        return Build(spec, approvalHandler);
    }

    private DisposableAgent CreateFromDefinition(
        AgentKey agentKey, string userId, AgentDefinition definition, IToolApprovalHandler approvalHandler)
    {
        var spec = AgentSpecProjection.ForAgent(definition, agentKey, userId, openRouterConfig, _logger);

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

        var effectiveClient = new ToolApprovalChatClient(
            chatClient, approvalHandler, spec.ConversationId, spec.WhitelistPatterns, agentPublisher);

        var featureConfig = new FeatureConfig(
            SubAgentFactory: def => CreateSubAgent(
                def, approvalHandler, spec.ConversationId, spec.WhitelistPatterns, spec.UserId),
            UserId: spec.UserId,
            ConversationContextProvider: () => ConversationContextMeta.Current);

        var domainTools = domainToolRegistry
            .GetToolsForFeatures(spec.EnabledFeatures, featureConfig)
            .ToList();
        var domainPrompts = domainToolRegistry
            .GetPromptsForFeatures(spec.EnabledFeatures)
            .ToList();

        var stateStore = spec.KeepsHistory
            ? serviceProvider.GetRequiredService<IThreadStateStore>()
            : new NullThreadStateStore();

        return new McpAgent(
            spec,
            effectiveClient,
            stateStore,
            agentPublisher,
            TimeProvider.System,
            domainTools,
            domainPrompts,
            loggerFactory,
            _promptCache);
    }

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
            attachmentSource: serviceProvider.GetService<IAttachmentSource>());
    }
}

public record OpenRouterConfig
{
    public required string ApiUrl { get; init; }
    public required string ApiKey { get; init; }
    public int? MaxContextTokens { get; init; }
    public ProviderRouting? ProviderRouting { get; init; }
    public IReadOnlyList<string>? PatchableModelIds { get; init; }
}

public sealed class AgentRegistryOptions
{
    public AgentDefinition[] Agents { get; set; } = [];
}