using Domain.DTOs.Household;
using Shouldly;

namespace Tests.Unit.Domain.DTOs;

// The app asks section's own rules, held on every save — the owners' page's and the operator's
// set-household-config alike: a cap is no fewer than none and no more than the section's ceiling, and an
// app's cap is keyed by a built app's id. The limit an ask meets is the app's own cap, else the household's,
// else the deployment's.
public class HouseholdAppAsksProblemsTests
{
    private static IReadOnlyList<string> ProblemsOf(AppAsksSection section) =>
        HouseholdSections.AppAsksSchema.Problems(HouseholdSections.AppAsksSchema.Write(section));

    [Fact]
    public void ASectionNeverSaved_SetsNoCap()
    {
        var read = HouseholdSections.AppAsksSchema.Read(HouseholdSections.AppAsksSchema.Default);

        read.DailyCap.ShouldBeNull();
        read.Apps.ShouldBeEmpty();
    }

    [Fact]
    public void CapsWithinTheirBounds_AreKeptAsSaved()
    {
        var section = new AppAsksSection
        {
            DailyCap = 0, Apps = new Dictionary<string, int> { ["a1b2c3"] = AppAsksSection.MaxDailyCap, ["d4e5f6"] = 0 }
        };

        ProblemsOf(section).ShouldBeEmpty();
        var read = HouseholdSections.AppAsksSchema.Read(HouseholdSections.AppAsksSchema.Write(section));
        read.DailyCap.ShouldBe(0);
        read.Apps.ShouldBe(section.Apps, ignoreOrder: true);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(AppAsksSection.MaxDailyCap + 1)]
    public void AHouseholdCapOutsideItsBounds_IsRefused(int cap) =>
        ProblemsOf(new AppAsksSection { DailyCap = cap }).ShouldHaveSingleItem().ShouldContain("household's daily cap");

    [Theory]
    [InlineData(-1)]
    [InlineData(AppAsksSection.MaxDailyCap + 1)]
    public void AnAppsCapOutsideItsBounds_IsRefused(int cap) =>
        ProblemsOf(new AppAsksSection { Apps = new Dictionary<string, int> { ["a1b2c3"] = cap } })
            .ShouldHaveSingleItem().ShouldContain("app a1b2c3's daily cap");

    [Fact]
    public void AnAppCapKeyedByNoBuiltAppsId_IsRefused() =>
        ProblemsOf(new AppAsksSection { Apps = new Dictionary<string, int> { ["../x"] = 5 } })
            .ShouldHaveSingleItem().ShouldContain("no built app's id");

    [Fact]
    public void TheLimit_IsTheAppsCap_ElseTheHouseholds_ElseTheDeployments()
    {
        var section = new AppAsksSection { DailyCap = 50, Apps = new Dictionary<string, int> { ["a1b2c3"] = 7 } };

        section.LimitOf("a1b2c3", deployment: 200).ShouldBe(7);
        section.LimitOf("d4e5f6", deployment: 200).ShouldBe(50);
        new AppAsksSection().LimitOf("a1b2c3", deployment: 200).ShouldBe(200);
    }
}