using Domain.DTOs.WebChat;

namespace Domain.DTOs.Household;

// How a household's spaces are listed, and where the topics of a space it no longer has are found.
//
// A removed space's topics are not moved: they stay under their own slug and are listed in the
// default space, read side (.claude/rules/channels.md, "Spaces"). So a removal is one save of the
// spaces section, nothing waits on Redis, and a space added back under the same slug has its topics
// again.
public static class SpaceListing
{
    // The order a person is shown them in: their configuration's, ties by slug.
    public static IReadOnlyList<HouseholdSpace> Ordered(IEnumerable<HouseholdSpace> spaces) =>
        [.. spaces.OrderBy(s => s.Order).ThenBy(s => s.Slug, StringComparer.Ordinal)];

    // The space that lists the topics of spaces the household no longer has: the default one, which
    // the admin API never removes; for a household whose operator saved spaces without it, its first.
    public static string? DefaultOf(IReadOnlyList<HouseholdSpace> spaces) =>
        spaces.Any(s => s.Slug == SpaceConfig.DefaultSlug)
            ? SpaceConfig.DefaultSlug
            : Ordered(spaces).FirstOrDefault()?.Slug;

    // The other slugs whose topics a page of this space lists beside its own: for the default space,
    // every slug holding topics that is none of the household's spaces now; for any other, none.
    public static IReadOnlyList<string> ListedWith(
        string slug, IReadOnlyList<HouseholdSpace> spaces, IEnumerable<string> holdingTopics) =>
        slug == DefaultOf(spaces)
            ? [.. holdingTopics.Where(held => spaces.All(s => s.Slug != held)).Distinct().Order(StringComparer.Ordinal)]
            : [];

    // The space a topic's news reaches: its own while the household has it, else the default space
    // that lists it now.
    public static string ShownIn(string topicSlug, IReadOnlyList<HouseholdSpace> spaces) =>
        spaces.Any(s => s.Slug == topicSlug) ? topicSlug : DefaultOf(spaces) ?? topicSlug;
}