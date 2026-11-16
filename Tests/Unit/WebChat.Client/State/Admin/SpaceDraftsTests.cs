using Domain.DTOs.Household;
using Shouldly;
using WebChat.Client.State.Admin;

namespace Tests.Unit.WebChat.Client.State.Admin;

// The spaces page changes only what the owner changed, so another owner's save merges space by space:
// a removal leaves every other space's order alone, a move swaps two, an added space goes last.
public sealed class SpaceDraftsTests
{
    private static readonly HouseholdSpace[] _loaded =
    [
        HouseholdSpace.Default,
        new() { Slug = "kitchen", Name = "Kitchen", Order = 1 },
        new() { Slug = "attic", Name = "Attic", Order = 2 },
        new() { Slug = "garden", Name = "Garden", Order = 3 }
    ];

    private static IEnumerable<(string, int)> Orders(SpaceDrafts drafts) =>
        drafts.ToSection().Spaces.Select(s => (s.Slug, s.Order));

    [Fact]
    public void RemovingASpace_LeavesTheOthersOrdersAsLoaded()
    {
        var drafts = new SpaceDrafts(_loaded);

        drafts.Remove(1);

        Orders(drafts).ShouldBe([("default", 0), ("attic", 2), ("garden", 3)]);
    }

    [Fact]
    public void MovingASpace_SwapsItsOrderWithTheOneItPassed_Only()
    {
        var drafts = new SpaceDrafts(_loaded);

        drafts.Move(2, -1);

        Orders(drafts).ShouldBe([("default", 0), ("attic", 1), ("kitchen", 2), ("garden", 3)]);
    }

    [Fact]
    public void AnAddedSpace_GoesAfterTheLast()
    {
        var drafts = new SpaceDrafts(_loaded);

        drafts.Remove(3);
        drafts.Add(" pantry ", " Pantry ");

        Orders(drafts).Last().ShouldBe(("pantry", 3));
    }

    // Two owners each adding a space at the end leaves two spaces under one order.
    [Fact]
    public void MovingOneOfTwoSpacesSharingAnOrder_StillMovesIt()
    {
        var drafts = new SpaceDrafts([HouseholdSpace.Default, new() { Slug = "a", Order = 1 }, new() { Slug = "b", Order = 1 }]);

        drafts.Move(2, -1);

        Orders(drafts).ShouldBe([("default", 0), ("b", 1), ("a", 2)]);
    }
}