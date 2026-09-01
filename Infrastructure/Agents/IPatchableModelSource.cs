using JetBrains.Annotations;

namespace Infrastructure.Agents;

// The models a config patch may name, asked at the moment a patch is checked rather than captured
// when the agent was built: part of the list is discovered while the agent runs, and a model that
// appeared after construction is still a model a person may pick.
[PublicAPI]
public interface IPatchableModelSource
{
    IReadOnlyList<string> Ids { get; }
}

[PublicAPI]
public sealed class FixedPatchableModelSource(IReadOnlyList<string> ids) : IPatchableModelSource
{
    public static FixedPatchableModelSource None { get; } = new([]);

    public IReadOnlyList<string> Ids => ids;
}