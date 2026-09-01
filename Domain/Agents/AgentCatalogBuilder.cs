using Domain.Contracts;
using Domain.DTOs;
using Domain.DTOs.Channel;

namespace Domain.Agents;

// The catalogue an agent registers with every channel, stamped with what each model accepts. It
// is built at registration time rather than held as a constant, so an hourly capability refresh
// and a minutely Lemonade discovery both reach channels through the registration that already
// exists.
public static class AgentCatalogBuilder
{
    public static IReadOnlyList<AgentCatalogEntry> Build(
        IReadOnlyList<AgentDefinition> agents,
        IReadOnlyList<PatchableModel> patchableModels,
        IModelCapabilityCatalog capabilities,
        ILemonadeModelSource lemonadeModels)
    {
        // The configured models first, then whatever the Lemonade chat host has: the host's
        // models are offered to every agent, and they carry what the host said they accept
        // rather than what the hosted provider's catalogue would answer for an unknown id.
        var patchable = patchableModels
            .Select(model => model with
            {
                AcceptedAttachmentKinds = capabilities.GetAcceptedAttachmentKinds(model.Id)
            })
            .Concat(LemonadeModelCatalog.ToPatchable(lemonadeModels.Current))
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