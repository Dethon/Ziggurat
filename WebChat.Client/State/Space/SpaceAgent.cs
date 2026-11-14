using Domain.DTOs.Channel;
using Domain.DTOs.Household;
using Domain.DTOs.WebChat;
using WebChat.Client.Contracts;

namespace WebChat.Client.State.Space;

// Which agent a space opens on: the one the person last picked in it, else the one the household's
// configuration names for it, else the one the household answers by default, else the first in the
// catalog. A pick is remembered per space; the default space keeps the key every space shared before
// spaces had agents, so nobody's pick there is lost, and a space they never picked in falls back on
// that last pick before the household's default.
public static class SpaceAgent
{
    private const string PickKey = "selectedAgentId";

    public static string PickKeyOf(string slug) =>
        slug == SpaceConfig.DefaultSlug ? PickKey : $"{PickKey}:{slug}";

    public static async Task<AgentCatalogEntry> ChooseAsync(
        ILocalStorageService storage, IReadOnlyList<AgentCatalogEntry> agents, SpaceState space)
    {
        var picked = Known(agents, await storage.GetAsync(PickKeyOf(space.CurrentSlug)));
        if ((picked ?? Known(agents, space.DefaultAgent)) is { } chosen)
        {
            return chosen;
        }

        var householdDefault = agents.FirstOrDefault(a => a.IsHouseholdDefault) ?? agents[0];
        return space.CurrentSlug == SpaceConfig.DefaultSlug
            ? householdDefault
            : Known(agents, await storage.GetAsync(PickKey)) ?? householdDefault;
    }

    // Where a space is: the default space is the front page, any other its slug.
    public static string PathOf(HouseholdSpace? space) =>
        space is null || space.Slug == SpaceConfig.DefaultSlug ? "/" : $"/{space.Slug}";

    private static AgentCatalogEntry? Known(IReadOnlyList<AgentCatalogEntry> agents, string? id) =>
        agents.FirstOrDefault(a => a.Id == id);
}