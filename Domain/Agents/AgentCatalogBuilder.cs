using Domain.Contracts;
using Domain.DTOs;
using Domain.DTOs.Channel;
using Domain.DTOs.Household;

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

    // The catalogue a person of a household picks from: its own agents as its configuration names them,
    // with what the deployment decides about models taken from the catalogue the agent registered — the
    // patchable models, the efforts, what a model accepts. An agent the agent registered under the same
    // id and on the same model keeps its entry, renamed, so the home's people see what was registered;
    // any other is the household's word on its model and the deployment's on what that model accepts,
    // none where the registered catalogue is silent (AttachmentCapability is permissive there).
    public static IReadOnlyList<AgentCatalogEntry> ForHousehold(
        HouseholdAgentNames household, IReadOnlyList<AgentCatalogEntry> registered,
        IReadOnlyList<PatchableModel>? householdLemonade = null)
    {
        var deployment = registered.FirstOrDefault();
        // A Lemonade model is its box's household's: the configured models, then the household's own
        // box's (the home's own or the deployment's), never the registered ones from the deployment's.
        IReadOnlyList<PatchableModel>? patchable = deployment?.PatchableModels is { } models
            ? [.. models.Where(m => !LemonadeModelId.IsLemonade(m.Id)), .. householdLemonade ?? []]
            : householdLemonade is { Count: > 0 } ? householdLemonade : null;
        var answersByDefault = household.Agents.Any(a => a.Id == household.DefaultAgent)
            ? household.DefaultAgent
            : household.Agents.FirstOrDefault()?.Id;
        return
        [
            .. household.Agents.Select(agent => Entry(agent) with { IsHouseholdDefault = agent.Id == answersByDefault })
        ];

        AgentCatalogEntry Entry(HouseholdAgentName agent) =>
                registered.FirstOrDefault(entry => entry.Id == agent.Id && entry.DefaultModel == agent.Model) is { } same
                    ? same with
                    {
                        Name = agent.Name,
                        Description = agent.Description,
                        DefaultReasoningEffort = agent.ReasoningEffort,
                        PatchableModels = patchable
                    }
                    : new AgentCatalogEntry(
                        agent.Id,
                        agent.Name,
                        agent.Description,
                        agent.Model,
                        agent.ReasoningEffort,
                        patchable,
                        deployment?.PatchableReasoningEfforts,
                        AcceptedBy(agent.Model, registered));
    }

    private static IReadOnlyList<AttachmentKind>? AcceptedBy(string? model, IReadOnlyList<AgentCatalogEntry> registered) =>
        model is null
            ? null
            : registered.FirstOrDefault(entry => entry.DefaultModel == model)?.DefaultModelAttachmentKinds
              ?? registered.SelectMany(entry => entry.PatchableModels ?? []).FirstOrDefault(m => m.Id == model)?.AcceptedAttachmentKinds;
}