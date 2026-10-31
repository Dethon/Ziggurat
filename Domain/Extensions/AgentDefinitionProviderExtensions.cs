using Domain.Contracts;
using Domain.DTOs.Channel;

namespace Domain.Extensions;

public static class AgentDefinitionProviderExtensions
{
    extension(IAgentDefinitionProvider provider)
    {
        // Fail open, deliberately and in one place: a turn with no agent id and a turn naming an
        // agent nobody configured both keep the feature. Only an agent that exists and does not
        // list the feature turns it off, so a typo in an id never silently disables memory.
        //
        // A custom agent is found only for its owner, so a caller with no owner to name reads only
        // the built-in agents' features.
        public bool HasFeatureEnabled(string? agentId, string feature, Account? owner = null) =>
            agentId is null
            || provider.GetById(agentId, owner) is not { } definition
            || definition.EnabledFeatures.Contains(feature, StringComparer.OrdinalIgnoreCase);
    }
}