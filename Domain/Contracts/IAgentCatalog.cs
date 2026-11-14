using Domain.DTOs.Channel;

namespace Domain.Contracts;

public interface IAgentCatalog
{
    IReadOnlyList<AgentCatalogEntry> GetAll();
    AgentCatalogEntry? Get(string agentId);
    bool Exists(string agentId);

    // A household's own Lemonade box's models, as last registered; none for a household with no box.
    IReadOnlyList<PatchableModel> LemonadeModelsOf(string householdId) => [];
}

public interface IMutableAgentCatalog : IAgentCatalog
{
    void Replace(IReadOnlyList<AgentCatalogEntry> agents,
        IReadOnlyDictionary<string, IReadOnlyList<PatchableModel>>? householdLemonadeModels = null);
}