using Domain.DTOs.Household;
using JetBrains.Annotations;

namespace Domain.DTOs.Admin;

// The household's chat-client spaces as its owners edit them (.claude/rules/admin.md, "Spaces"), in
// their order, beside the household's agents a space may open on — the very ones its turns run, under
// their names. A save sends the spaces section whole.
[PublicAPI]
public sealed record AdminSpaces(IReadOnlyList<HouseholdSpace> Spaces, IReadOnlyList<AdminSpaceAgent> Agents)
{
    public const int NameLength = 100;
}

[PublicAPI]
public sealed record AdminSpaceAgent(string Id, string Name);