using Domain.Agents;
using Domain.Contracts;

namespace Infrastructure.Agents;

// The models a config patch may name: the configured patchable ids, then whatever the Lemonade
// chat host has at the moment of asking, under the host's namespace. Read per turn rather than
// captured, which is what lets a model loaded on the box after the agent was built be picked.
public sealed class PatchableModelWhitelist(
    IReadOnlyList<string> configured,
    ILemonadeModelSource lemonadeModels) : IPatchableModelSource
{
    public IReadOnlyList<string> Ids =>
        [.. configured, .. lemonadeModels.Current.Select(m => LemonadeModelId.Namespaced(m.Id))];
}