using Domain.Contracts;
using Domain.DTOs.Channel;

namespace Domain.Agents;

public sealed class MutableAgentCatalog : IMutableAgentCatalog
{
    private volatile IReadOnlyList<AgentCatalogEntry> _agents = [];

    public IReadOnlyList<AgentCatalogEntry> GetAll() => _agents;

    public AgentCatalogEntry? Get(string agentId) => _agents.FirstOrDefault(a => a.Id == agentId);

    public bool Exists(string agentId) => _agents.Any(a => a.Id == agentId);

    private volatile IReadOnlyDictionary<string, IReadOnlyList<PatchableModel>> _lemonade =
        new Dictionary<string, IReadOnlyList<PatchableModel>>();

    public IReadOnlyList<PatchableModel> LemonadeModelsOf(string householdId) =>
        _lemonade.GetValueOrDefault(householdId) ?? [];

    public void Replace(
        IReadOnlyList<AgentCatalogEntry> agents,
        IReadOnlyDictionary<string, IReadOnlyList<PatchableModel>>? householdLemonadeModels = null)
    {
        _agents = [.. agents];
        _lemonade = householdLemonadeModels ?? new Dictionary<string, IReadOnlyList<PatchableModel>>();
    }
}