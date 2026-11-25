using Domain.DTOs.Household;
using Shouldly;

namespace Tests.Unit.Domain.DTOs;

// The spaces section's own rules for an app space, held on every save — the owners' page's and the
// operator's set-household-config alike: it names a built app's id, one app is one space, it opens on
// no agent, and the default space, where removed spaces' conversations are listed, shares none.
public class HouseholdSpacesProblemsTests
{
    private static readonly HouseholdSpace _shared = new() { Slug = "glucose", Name = "Glucose", AppId = "a1b2c3", Order = 1 };

    private static IReadOnlyList<string> ProblemsOf(params HouseholdSpace[] spaces) =>
        HouseholdSections.SpacesSchema.Problems(HouseholdSections.SpacesSchema.Write(new SpacesSection { Spaces = spaces }));

    [Fact]
    public void AnAppSpace_IsASpaceLikeAnyOther() => ProblemsOf(HouseholdSpace.Default, _shared).ShouldBeEmpty();

    [Fact]
    public void AnAppIdOfNoBuiltAppsShape_IsRefused() =>
        ProblemsOf(HouseholdSpace.Default, _shared with { AppId = "../x" }).ShouldHaveSingleItem().ShouldContain("no built app's id");

    [Fact]
    public void OneAppSharedByTwoSpaces_IsRefused() =>
        ProblemsOf(HouseholdSpace.Default, _shared, _shared with { Slug = "again", Order = 2 })
            .ShouldHaveSingleItem().ShouldContain("app a1b2c3 is shared by more than one space");

    [Fact]
    public void AnAppSpaceOpeningOnAnAgent_IsRefused() =>
        ProblemsOf(HouseholdSpace.Default, _shared with { DefaultAgent = "jonas" }).ShouldHaveSingleItem().ShouldContain("opens on no agent");

    [Fact]
    public void TheDefaultSpace_SharesNoApp() =>
        ProblemsOf(HouseholdSpace.Default with { AppId = "a1b2c3" }).ShouldHaveSingleItem().ShouldContain("the default space");
}