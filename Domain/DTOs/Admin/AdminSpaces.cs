using System.Text.Json.Serialization;
using Domain.Apps;
using Domain.DTOs.Household;
using JetBrains.Annotations;

namespace Domain.DTOs.Admin;

// The household's chat-client spaces as its owners edit them (.claude/rules/admin-owner-pages.md, "Spaces"), in
// their order, beside the household's agents a space may open on — the very ones its turns run, under
// their names, and the household's published apps one may share as an app space — none when the household
// has no apps feature, null when the apps server could not be asked. An app space's name and colour are
// its app's, as the apps server answers them now; its approved reach is only ever what an owner's save
// sent. A save sends the spaces section whole.
[PublicAPI]
public sealed record AdminSpaces(
    IReadOnlyList<HouseholdSpace> Spaces, IReadOnlyList<AdminSpaceAgent> Agents, IReadOnlyList<AdminSpaceApp>? Apps = null)
{
    public const int NameLength = 100;

    public const string AppsUnavailableMessage = "The household's apps could not be read just now. Try again in a moment.";

    // What an app space's app asks, as its published version says, beyond the reach an owner approved: what
    // waits on an owner saving the space approving it. None for a chat space, or an app not listed just now.
    public AppReach? PendingOf(HouseholdSpace space) =>
        space.AppId is { } id && Apps?.FirstOrDefault(app => app.Id == id) is { } app
            ? app.Reach.Beyond(space.Approved)
            : null;
}

// A published app as an owner sees it before sharing it with the household: who made it (by their name
// in the household now), what of the household it reads and which outside origins it talks to, as its
// served commit says.
[PublicAPI]
public sealed record AdminSpaceApp(
    string Id,
    string Slug,
    string Name,
    string AccentColor,
    string Description,
    string AuthorId,
    string AuthorName,
    IReadOnlyList<string> Reads,
    IReadOnlyList<string> Connect,
    string Url)
{
    // All it asks, which an owner approves by saving its space with it.
    [JsonIgnore]
    public AppReach Reach => new() { Reads = Reads, Connect = Connect };
}

[PublicAPI]
public sealed record AdminSpaceAgent(string Id, string Name);