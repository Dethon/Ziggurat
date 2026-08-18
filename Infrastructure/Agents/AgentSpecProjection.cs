using Domain.Agents;
using Domain.DTOs;
using Domain.Prompts;
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
            AgentId = definition.Id,
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
            UsesOutposts = definition.UsesOutposts,
            EnabledFeatures = definition.EnabledFeatures,
            FilesystemEnabledTools = ExtractFilesystemEnabledTools(definition.EnabledFeatures),
            WhitelistPatterns = definition.WhitelistPatterns,
            CustomInstructions = definition.CustomInstructions,
            PromptSections = ResolveSections(definition.Id, definition.PromptSections),
            Language = definition.Language,
            KeepsHistory = true,
            RecordsOutpostVerdicts = true,
            PatchableModelIds = openRouterConfig.PatchableModelIds ?? []
        };

    public static AgentSpec ForSubAgent(
        SubAgentDefinition definition,
        SpawnContext spawn,
        OpenRouterConfig openRouterConfig,
        ILogger? logger)
    {
        var identity = $"subagent-{definition.Id}";
        // A subagent cannot spawn subagents.
        string[] enabledFeatures = [.. definition.EnabledFeatures
            .Where(f => !f.Equals("subagents", StringComparison.OrdinalIgnoreCase))];

        return new AgentSpec
        {
            AgentId = definition.Id,
            DisplayName = identity,
            Description = definition.Description ?? "",
            MetricsAgentId = definition.Name,
            // Fresh every spawn, so a subagent never shares the parent's prompt cache: its
            // static prefix is its own instructions and its own tools, and sticking it to the
            // parent's session would route the two to the same cached prefix.
            RoutingSessionId = $"{identity}:{Guid.NewGuid():N}",
            // The parent's conversation, deliberately: a subagent acts on the parent's behalf,
            // so its metrics answer "which conversation was this slow subagent running in".
            ConversationId = spawn.ConversationId,
            UserId = spawn.UserId,
            Model = definition.Model,
            MaxContextTokens = definition.MaxContextTokens ?? openRouterConfig.MaxContextTokens,
            ReasoningEffort = definition.ReasoningEffort,
            ProviderRouting = ProviderRoutingResolver.Resolve(
                definition.ProviderRouting, openRouterConfig.ProviderRouting,
                definition.Model, identity, logger),
            McpServerEndpoints = [.. definition.McpServerEndpoints.Select(McpServerEndpoint.Configured)],
            // Two yeses, and neither alone is enough. The parent's is the ceiling — a subagent
            // acts on its behalf and cannot reach a machine it could not — and the definition's
            // is what keeps a narrow worker off the machines: the list of subagents is shared by
            // every agent that enables the feature, so inheriting the parent's flag alone would
            // hand a newly added profile every registered laptop. What is inherited is the flag
            // and never a set of machines: the session build below asks the registry itself, so
            // a subagent mounts whatever is live when it is spawned. See docs/adr/0028.
            UsesOutposts = spawn.UsesOutposts && definition.UsesOutposts,
            EnabledFeatures = enabledFeatures,
            FilesystemEnabledTools = ExtractFilesystemEnabledTools(enabledFeatures),
            WhitelistPatterns = spawn.WhitelistPatterns,
            CustomInstructions = definition.CustomInstructions,
            PromptSections = ResolveSections(definition.Id, definition.PromptSections),
            Language = definition.Language,
            KeepsHistory = false,
            // A subagent mounts the machines it was given and judges none of them. The verdict is
            // the answer to "did the agent you registered with mount you", and a subagent is not
            // that agent: its endpoint list is its own, so it can reach a different verdict for
            // the same machine, and writing it would report a collision inside somebody's
            // delegated task as the operator's name being taken.
            RecordsOutpostVerdicts = false,
            // A config patch names a model from the parent's whitelist and an effort chosen for
            // the parent's job; a subagent runs the model its own definition configures, which
            // is the point of having one. No patch reaches a subagent today, so this is the
            // second line of defence: if a future change ever copies the parent's message
            // properties down, the patch is rejected and logged instead of silently winning.
            PatchableModelIds = []
        };
    }

    // A name nothing declares is a configuration error and is refused here, where the name was
    // written. Assembling the rest and going on would start an agent missing exactly the behaviour
    // somebody added a line to give it, and nothing downstream would ever mention it again.
    private static IReadOnlyList<PromptSection> ResolveSections(string agentId, IEnumerable<string> names)
    {
        return
        [
            .. names.Select(name => PromptManifest.Selected(name) ?? throw new InvalidOperationException(
                $"Agent '{agentId}' names prompt section '{name}', which the manifest does not " +
                $"declare as selectable. Available: {string.Join(", ", PromptManifest.SelectableSections)}."))
        ];
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