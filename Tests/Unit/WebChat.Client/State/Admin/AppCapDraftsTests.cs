using Domain.DTOs.Admin;
using Domain.DTOs.Household;
using Shouldly;
using WebChat.Client.State.Admin;

namespace Tests.Unit.WebChat.Client.State.Admin;

// The apps page shows a cap for each of the household's published apps, and after them any cap kept for an
// app no longer listed, by its id, so it can be removed; a save sends only the caps set, so an app whose cap
// was cleared falls back to the household's, and a page changing nothing sends what it loaded.
public sealed class AppCapDraftsTests
{
    private static readonly AdminCappedApp _glucose = new("a1b2c3", "Glucose today", "#e9601f", "Ana", "https://glucose-a1b2c3.apps.test/");
    private static readonly AdminCappedApp _carbs = new("d4e5f6", "Carbs", "#2a9d8f", "Bea", "https://carbs-d4e5f6.apps.test/");

    private static readonly AppAsksSection _caps = new()
    {
        DailyCap = 50, Apps = new Dictionary<string, int> { ["d4e5f6"] = 5, ["g7h8i9"] = 2 }
    };

    [Fact]
    public void EveryPublishedApp_IsListedWithItsCap_ThenCapsOfAppsNoLongerListed()
    {
        var drafts = AppCapDrafts.Of(new AdminAppAsks(_caps, [_glucose, _carbs]));

        drafts.Household.ShouldBe(50);
        drafts.Apps.Select(a => (a.Id, a.Name, a.Cap, a.Listed)).ShouldBe(
        [
            ("a1b2c3", "Glucose today", (int?)null, true),
            ("d4e5f6", "Carbs", 5, true),
            ("g7h8i9", "g7h8i9", 2, false)
        ]);
    }

    [Fact]
    public void AnUnchangedPage_SendsWhatItLoaded() =>
        AppCapDrafts.Of(new AdminAppAsks(_caps, [_glucose, _carbs])).ToSection().ShouldBe(_caps, new SectionComparer());

    [Fact]
    public void ACapCleared_IsLeftOut_AndOneSet_IsSent()
    {
        var drafts = AppCapDrafts.Of(new AdminAppAsks(_caps, [_glucose, _carbs]));

        drafts.Household = null;
        drafts.Apps[0].Cap = 0;
        drafts.Apps[2].Cap = null;

        var sent = drafts.ToSection();
        sent.DailyCap.ShouldBeNull();
        sent.Apps.ShouldBe(new Dictionary<string, int> { ["a1b2c3"] = 0, ["d4e5f6"] = 5 }, ignoreOrder: true);
    }

    // The apps server not answering lists no app: every cap kept is shown by its id.
    [Fact]
    public void NoListing_ShowsEveryCapKeptByItsId() =>
        AppCapDrafts.Of(new AdminAppAsks(_caps)).Apps.Select(a => (a.Id, a.Listed)).ShouldBe([("d4e5f6", false), ("g7h8i9", false)]);

    private sealed class SectionComparer : IEqualityComparer<AppAsksSection>
    {
        public bool Equals(AppAsksSection? x, AppAsksSection? y) =>
            x?.DailyCap == y?.DailyCap && x!.Apps.OrderBy(a => a.Key).SequenceEqual(y!.Apps.OrderBy(a => a.Key));

        public int GetHashCode(AppAsksSection obj) => 0;
    }
}