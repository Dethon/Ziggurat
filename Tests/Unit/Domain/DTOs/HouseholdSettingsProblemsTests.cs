using Domain.DTOs.Household;
using Shouldly;

namespace Tests.Unit.Domain.DTOs;

// The settings' problems are shown to an owner on the settings page as they are written, under the
// field's own label, so they speak of "the time zone" rather than a document path: an owner who typed
// "Madrid" was answered "settings.timeZone 'Madrid' is no time zone".
public class HouseholdSettingsProblemsTests
{
    private static IReadOnlyList<string> ProblemsOf(HouseholdSettings settings) =>
        HouseholdSections.SettingsSchema.Problems(HouseholdSections.SettingsSchema.Write(settings));

    [Fact]
    public void AZoneThatIsNone_IsNamedAsThePageLabelsIt()
    {
        var problems = ProblemsOf(new HouseholdSettings { Name = "Casa Rivas", TimeZone = "Madrid" });

        problems.ShouldHaveSingleItem().ShouldStartWith("The time zone 'Madrid'");
        problems.ShouldAllBe(p => !p.Contains("settings."));
    }

    [Fact]
    public void ANameAndALanguageRefused_SpeakAsThePageDoes()
    {
        var problems = ProblemsOf(new HouseholdSettings { Name = " ", TimeZone = "Europe/Madrid", Language = "español!" });

        problems.Count.ShouldBe(2);
        problems.ShouldContain(p => p.StartsWith("The name"));
        problems.ShouldContain(p => p.StartsWith("The language 'español!'"));
        problems.ShouldAllBe(p => !p.Contains("settings."));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(HouseholdSettings.MaxToolRoundsLimit + 1)]
    public void ToolRoundsOutsideTheirRange_AreNamedAsThePageLabelsThem(int rounds)
    {
        var problems = ProblemsOf(new HouseholdSettings { Name = "Casa Rivas", TimeZone = "Europe/Madrid", MaxToolRounds = rounds });

        problems.ShouldHaveSingleItem().ShouldStartWith("The tool rounds per turn");
    }

    [Theory]
    [InlineData(null)]
    [InlineData(1)]
    [InlineData(HouseholdSettings.MaxToolRoundsLimit)]
    public void NoToolRoundsOrOnesInTheirRange_AreNoProblem(int? rounds) =>
        ProblemsOf(new HouseholdSettings { Name = "Casa Rivas", TimeZone = "Europe/Madrid", MaxToolRounds = rounds })
            .ShouldBeEmpty();
}