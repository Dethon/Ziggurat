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
        Order = _drafts.Count == 0 ? 0 : _drafts.Max(d => d.Order) + 1
    });

    public void Remove(int index) => _drafts.RemoveAt(index);

    public SpacesSection ToSection() => new()
    {
        Spaces =
        [
            .. _drafts.Select(draft => new HouseholdSpace
            {
                Slug = draft.Slug,
                Name = draft.Name.Trim(),
                AccentColor = draft.AccentColor.Trim(),
                DefaultAgent = string.IsNullOrEmpty(draft.DefaultAgent) ? null : draft.DefaultAgent,
                Order = draft.Order
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

    public static SpaceDraft Of(HouseholdSpace space) => new()
    {
        Slug = space.Slug,
        SavedName = space.Name,
        Name = space.Name,
        AccentColor = space.AccentColor,
        DefaultAgent = space.DefaultAgent ?? "",
        Order = space.Order
    };
}