using Domain.Contracts;
using Domain.DTOs;
using Domain.DTOs.Channel;

namespace Domain.Agents;

// The catalogue an agent registers with every channel, stamped with what each model accepts. It
// is built at registration time rather than held as a constant, so an hourly capability refresh
// reaches channels through the registration that already exists.
public static class AgentCatalogBuilder
{
    public static IReadOnlyList<AgentCatalogEntry> Build(
        IReadOnlyList<AgentDefinition> agents,
        IReadOnlyList<PatchableModel> patchableModels,
        IModelCapabilityCatalog capabilities)
    {
        var patchable = patchableModels
            .Select(model => model with
            {
                AcceptedAttachmentKinds = capabilities.GetAcceptedAttachmentKinds(model.Id)
            })
            .ToList();

        return agents
            .Select(agent => new AgentCatalogEntry(
                agent.Id,
                agent.Name,
                agent.Description,
                agent.Model,
                agent.ReasoningEffort,
                patchable,
                AgentConfigPatch.SupportedEfforts,
                capabilities.GetAcceptedAttachmentKinds(agent.Model)))
            .ToList();
    }
}