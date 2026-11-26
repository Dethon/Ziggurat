using Domain.Apps;
using Domain.DTOs.Admin;
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

    private static readonly AdminSpaceApp _glucose = new(
        "a1b2c3", "glucose", "Glucose today", "#e9601f", "My glucose today.", "user-bea", "Bea", ["/sugary"], [],
        "https://glucose-a1b2c3.apps.example/");

    // An app space is the app's name and colour, goes after the last space, opens on no agent and keeps
    // the app it shares through a save, as a chat space keeps what it is. Sharing it approves the reach the
    // page showed of it, and no more.
    [Fact]
    public void ASharedApp_IsAnAppSpaceAfterTheLast_KeptThroughTheSection()
    {
        var drafts = new SpaceDrafts(_loaded);

        drafts.Share(_glucose);

        drafts.ToSection().Spaces.Last().ShouldBe(new HouseholdSpace
        {
            Slug = "glucose", Name = "Glucose today", AccentColor = "#e9601f", AppId = "a1b2c3", Order = 4,
            Approved = new AppReach { Reads = ["/sugary"] }
        });
    }

    // Its approved reach too: a save sending none against the one loaded would be merged as an owner taking
    // the approval away.
    [Fact]
    public void AnAppSpaceLoaded_KeepsItsAppAndItsApprovalThroughASave()
    {
        var loaded = new HouseholdSpace
        {
            Slug = "glucose", Name = "Glucose today", AccentColor = "#e9601f", AppId = "a1b2c3", Order = 4,
            Approved = new AppReach { Reads = ["/sugary"] }
        };
        var drafts = new SpaceDrafts([.. _loaded, loaded]);

        drafts.Move(4, -1);

        drafts.ToSection().Spaces.ShouldContain(loaded with { Order = 3 });
    }

    // A space's address is set when it is added and never edited, so an app takes its own slug unless a
    // space already has it.
    [Fact]
    public void AnAppWhoseSlugASpaceHas_TakesTheFirstFreeOne()
    {
        var drafts = new SpaceDrafts([.. _loaded, new HouseholdSpace { Slug = "glucose", Name = "Sugar", Order = 4 }]);

        drafts.Share(_glucose);

        drafts.ToSection().Spaces.Last().Slug.ShouldBe("glucose-2");
    }

    // What the page offers to share: the household's published apps no space shares yet.
    [Fact]
    public void TheAppsOffered_AreThoseNoSpaceSharesYet()
    {
        var lights = _glucose with { Id = "d4e5f6", Slug = "lights", Name = "Lights" };
        var drafts = new SpaceDrafts(_loaded);

        drafts.Share(_glucose);

        drafts.Shareable([_glucose, lights]).ShouldBe([lights]);
    }

    // A publish asking for more than an owner approved is shown as waiting on one; approving it on the page
    // sends all the app asks as the space's approval, and nothing is shown waiting after.
    [Fact]
    public void ApprovingAnAppThatAsksForMore_SendsAllItAsks_AndLeavesNothingPending()
    {
        var wider = _glucose with { Reads = ["/sugary", "/notes"], Connect = ["https://tracker.example.com"] };
        var shared = new HouseholdSpace
        {
            Slug = "glucose", Name = "Glucose today", AccentColor = "#e9601f", AppId = "a1b2c3", Order = 4,
            Approved = new AppReach { Reads = ["/sugary"] }
        };
        var loaded = new AdminSpaces([.. _loaded, shared], [], [wider]);
        var drafts = new SpaceDrafts(loaded.Spaces);
        var draft = drafts.All[4];
        var before = drafts.PendingOf(draft, loaded);

        draft.Approve(wider);

        before.ShouldBe(new AppReach { Reads = ["/notes"], Connect = ["https://tracker.example.com"] });
        drafts.PendingOf(draft, loaded).ShouldBe(AppReach.None);
        drafts.ToSection().Spaces[4].Approved.ShouldBe(new AppReach { Reads = ["/sugary", "/notes"], Connect = ["https://tracker.example.com"] });
    }

    // The servers an app's asks may use are approved as its reads are: sharing it approves those the page
    // showed, a later publish asking another is shown waiting, and approving it sends them all.
    [Fact]
    public void TheServersAnAppsAsksMayUse_AreApprovedWithTheRestOfWhatItAsks()
    {
        var asking = _glucose with { Asks = ["sugary"] };
        var wider = asking with { Asks = ["sugary", "vault"] };
        var drafts = new SpaceDrafts(_loaded);
        drafts.Share(asking);
        var shared = drafts.ToSection().Spaces.Last();
        var loaded = new AdminSpaces([.. _loaded, shared], [], [wider]);
        var reloaded = new SpaceDrafts(loaded.Spaces);
        var draft = reloaded.All[4];
        var before = reloaded.PendingOf(draft, loaded);

        draft.Approve(wider);

        shared.Approved.ShouldBe(new AppReach { Reads = ["/sugary"], Asks = ["sugary"] });
        before.ShouldBe(new AppReach { Asks = ["vault"] });
        reloaded.PendingOf(draft, loaded).ShouldBe(AppReach.None);
        reloaded.ToSection().Spaces[4].Approved.ShouldBe(new AppReach { Reads = ["/sugary"], Asks = ["sugary", "vault"] });
    }
}