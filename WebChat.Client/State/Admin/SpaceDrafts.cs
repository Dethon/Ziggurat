using Domain.DTOs.Admin;
using Domain.DTOs.Household;
using Domain.DTOs.WebChat;

namespace WebChat.Client.State.Admin;

// The spaces page's list between loading and saving: what the owner changed, and only that. Each space
// keeps the order it was loaded with — a move swaps two spaces' orders, an added space takes the next
// one — because the admin API merges two owners' saves space by space: the page used to number every
// space again by its place in the list, so removing one space changed the order of every space after
// it, and another owner removing a different space at the same time was refused for a change nobody made.
public sealed class SpaceDrafts
{
    private readonly List<SpaceDraft> _drafts;

    public SpaceDrafts(IEnumerable<HouseholdSpace> loaded) => _drafts = [.. loaded.Select(SpaceDraft.Of)];

    public IReadOnlyList<SpaceDraft> All => _drafts;

    public void Move(int index, int by)
    {
        var other = index + by;
        if (_drafts[index].Order == _drafts[other].Order)
        {
            // Two spaces sharing an order (two owners each added one at the end) cannot be told apart
            // by swapping, so the list is numbered by its places first.
            foreach (var (draft, place) in _drafts.Select((draft, place) => (draft, place)))
            {
                draft.Order = place;
            }
        }

        (_drafts[index].Order, _drafts[other].Order) = (_drafts[other].Order, _drafts[index].Order);
        (_drafts[index], _drafts[other]) = (_drafts[other], _drafts[index]);
    }

    public void Add(string slug, string name) => _drafts.Add(new SpaceDraft
    {
        Slug = slug.Trim(),
        Name = name.Trim(),
        AccentColor = SpaceConfig.DefaultAccentColor,
        Order = NextOrder
    });

    public void Remove(int index) => _drafts.RemoveAt(index);

    // A published app shared with the household as an app space: its name and colour (the admin API
    // stamps them again from the app), after the last space, at the app's own slug unless a space has it.
    public void Share(AdminSpaceApp app) => _drafts.Add(new SpaceDraft
    {
        Slug = FreeSlug(app.Slug),
        Name = app.Name,
        AccentColor = app.AccentColor,
        AppId = app.Id,
        Order = NextOrder
    });

    // The household's published apps no space shares yet.
    public IReadOnlyList<AdminSpaceApp> Shareable(IEnumerable<AdminSpaceApp> apps) =>
        [.. apps.Where(app => _drafts.All(draft => draft.AppId != app.Id))];

    private int NextOrder => _drafts.Count == 0 ? 0 : _drafts.Max(d => d.Order) + 1;

    private string FreeSlug(string slug) =>
        Enumerable.Range(1, _drafts.Count + 1)
            .Select(n => n == 1 ? slug : $"{slug}-{n}")
            .First(candidate => _drafts.All(draft => draft.Slug != candidate));

    public SpacesSection ToSection() => new()
    {
        Spaces =
        [
            .. _drafts.Select(draft => new HouseholdSpace
            {
                Slug = draft.Slug,
                Name = draft.Name.Trim(),
                AccentColor = draft.AccentColor.Trim(),
                DefaultAgent = string.IsNullOrEmpty(draft.DefaultAgent) || draft.AppId is not null ? null : draft.DefaultAgent,
                Order = draft.Order,
                AppId = draft.AppId
            })
        ]
    };
}

public sealed class SpaceDraft
{
    public string Slug { get; init; } = "";

    // What the space is called as saved, so an article keeps its name while its field is edited;
    // none for a space added here and not saved yet.
    public string? SavedName { get; init; }

    public string Name { get; set; } = "";
    public string AccentColor { get; set; } = "";
    public string DefaultAgent { get; set; } = "";
    public int Order { get; set; }

    // The built app an app space shares, set when it is added; none for a chat space.
    public string? AppId { get; init; }

    public static SpaceDraft Of(HouseholdSpace space) => new()
    {
        Slug = space.Slug,
        SavedName = space.Name,
        Name = space.Name,
        AccentColor = space.AccentColor,
        DefaultAgent = space.DefaultAgent ?? "",
        Order = space.Order,
        AppId = space.AppId
    };
}