using Domain.Agents;
using Domain.DTOs;
using Domain.Tools.FileSystem;
using Microsoft.Extensions.Logging;

namespace Infrastructure.Agents;

// The two entry points read side by side. A difference between a top-level agent and a
// subagent belongs here, as a field with a value, and never downstream as an argument one
// path stops passing.
internal static class AgentSpecProjection
{
    public static AgentSpec ForAgent(
        AgentDefinition definition,
        AgentKey agentKey,
        string userId,
        OpenRouterConfig openRouterConfig,
        ILogger? logger) => new()
        {
            DisplayName = $"{definition.Name}-{agentKey.ConversationId}",
            Description = definition.Description ?? "",
            MetricsAgentId = definition.Name,
            RoutingSessionId = $"{definition.Id}:{agentKey.ConversationId}",
            ConversationId = agentKey.ConversationId,
            UserId = userId,
            Model = definition.Model,
            MaxContextTokens = definition.MaxContextTokens ?? openRouterConfig.MaxContextTokens,
            ReasoningEffort = definition.ReasoningEffort,
            ProviderRouting = ProviderRoutingResolver.Resolve(
                definition.ProviderRouting, openRouterConfig.ProviderRouting,
                definition.Model, definition.Id, logger),
            // Everything a definition names came out of the deployment's own settings, so
            // everything composed here is configured. Dynamic endpoints — live outposts — are
            // merged in downstream, where the registry of them is reachable.
            McpServerEndpoints = [.. definition.McpServerEndpoints.Select(McpServerEndpoint.Configured)],
            EnabledFeatures = definition.EnabledFeatures,
            FilesystemEnabledTools = ExtractFilesystemEnabledTools(definition.EnabledFeatures),
            WhitelistPatterns = definition.WhitelistPatterns,
            CustomInstructions = definition.CustomInstructions,
            Language = definition.Language,
            KeepsHistory = true,
            PatchableModelIds = openRouterConfig.PatchableModelIds ?? []
        };

    public static AgentSpec ForSubAgent(
        SubAgentDefinition definition,
        string conversationId,
        string[] whitelistPatterns,
        string userId,
        OpenRouterConfig openRouterConfig,
        ILogger? logger)
    {
        var identity = $"subagent-{definition.Id}";
        // A subagent cannot spawn subagents.
        string[] enabledFeatures = [.. definition.EnabledFeatures
            .Where(f => !f.Equals("subagents", StringComparison.OrdinalIgnoreCase))];

        return new AgentSpec
        {
            DisplayName = identity,
            Description = definition.Description ?? "",
            MetricsAgentId = definition.Name,
            // Fresh every spawn, so a subagent never shares the parent's prompt cache: its
            // static prefix is its own instructions and its own tools, and sticking it to the
            // parent's session would route the two to the same cached prefix.
            RoutingSessionId = $"{identity}:{Guid.NewGuid():N}",
            // The parent's conversation, deliberately: a subagent acts on the parent's behalf,
            // so its metrics answer "which conversation was this slow subagent running in".
            ConversationId = conversationId,
            UserId = userId,
            Model = definition.Model,
            MaxContextTokens = definition.MaxContextTokens ?? openRouterConfig.MaxContextTokens,
            ReasoningEffort = definition.ReasoningEffort,
            ProviderRouting = ProviderRoutingResolver.Resolve(
                definition.ProviderRouting, openRouterConfig.ProviderRouting,
                definition.Model, identity, logger),
            McpServerEndpoints = [.. definition.McpServerEndpoints.Select(McpServerEndpoint.Configured)],
            EnabledFeatures = enabledFeatures,
            FilesystemEnabledTools = ExtractFilesystemEnabledTools(enabledFeatures),
            WhitelistPatterns = whitelistPatterns,
            CustomInstructions = definition.CustomInstructions,
            Language = definition.Language,
            KeepsHistory = false,
            // A config patch names a model from the parent's whitelist and an effort chosen for
            // the parent's job; a subagent runs the model its own definition configures, which
            // is the point of having one. No patch reaches a subagent today, so this is the
            // second line of defence: if a future change ever copies the parent's message
            // properties down, the patch is rejected and logged instead of silently winning.
            PatchableModelIds = []
        };
    }

    private static IReadOnlySet<string> ExtractFilesystemEnabledTools(IEnumerable<string> enabledFeatures)
    {
        var fsParts = enabledFeatures
            .Select(f => f.Split('.', 2))
            .Where(p => p[0].Equals("filesystem", StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (fsParts.Count == 0)
        {
            return new HashSet<string>();
        }

        if (fsParts.Any(p => p.Length == 1))
        {
            return FileSystemToolFeature.AllToolKeys;
        }

        return fsParts
            .Where(p => p.Length == 2)
            .Select(p => p[1])
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }
}