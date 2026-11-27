using System.Text.Json.Serialization;
using Domain.Apps;
using Domain.DTOs.Household;
using JetBrains.Annotations;

namespace Domain.DTOs.WebChat;

// One place WebChat's space switcher offers: a chat space (its slug, opened in the chat client), an app
// space (its slug and its app's link, opened as a page of its own origin) or one of the person's own
// published apps no space shares (its link, Private).
[PublicAPI]
public sealed record SwitcherEntry(string Name, string AccentColor, string? Slug, string? Url, bool Private = false)
{
    [JsonIgnore]
    public bool IsApp => Url is not null;
}

// What the switcher lists: the household's spaces in its order, chat and app spaces alike, then the
// person's private apps by name. An app space is listed only with its link, which the apps server alone
// knows: one it could not be asked for is left out rather than offered as a place that opens nothing.
public static class SpaceSwitcher
{
    public static IReadOnlyList<SwitcherEntry> Entries(IEnumerable<HouseholdSpace> spaces, IReadOnlyList<PersonsApp>? apps)
    {
        var ordered = SpaceListing.Ordered(spaces);
        var shared = ordered.Where(s => s.IsApp).Select(s => s.AppId!).ToHashSet(StringComparer.Ordinal);
        var links = (apps ?? []).ToDictionary(a => a.Id, a => a.Url, StringComparer.Ordinal);
        return
        [
            .. ordered
                .Where(s => !s.IsApp || links.ContainsKey(s.AppId!))
                .Select(s => new SwitcherEntry(s.Name, s.AccentColor, s.Slug, s.IsApp ? links[s.AppId!] : null)),
            .. (apps ?? [])
                .Where(a => a.Own && !shared.Contains(a.Id))
                .OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase)
                .Select(a => new SwitcherEntry(a.Name, a.AccentColor, Slug: null, a.Url, Private: true))
        ];
    }
}