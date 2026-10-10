using Shouldly;

namespace Tests.Eval.Fixtures;

// The research corpus is a set of numbers that have to agree with each other, because the
// scenarios mark replies against figures derived from them: a table that did not sum to the
// year's total would make the right answer a wrong one.
public class SolarCorpusTests
{
    [Fact]
    public void TheMonthlyTable_SumsToTheYearTheReportStates()
    {
        SolarCorpus.Monthly2025.Count.ShouldBe(12);
        SolarCorpus.Monthly2025.Sum().ShouldBe(SolarCorpus.Production2025);
    }

    [Fact]
    public void TheThirdQuarter_IsTheBest_AndItsShareIsTheOneScenariosAskFor()
    {
        var quarters = SolarCorpus.Monthly2025.Chunk(3).Select(quarter => quarter.Sum()).ToList();

        quarters.IndexOf(quarters.Max()).ShouldBe(2);
        quarters[2].ShouldBe(SolarCorpus.BestQuarter);
        (100d * SolarCorpus.BestQuarter / SolarCorpus.Production2025).ShouldBe(34.5, 0.05);
    }

    [Fact]
    public void TheDerivedFigures_FollowFromThePlantedOnes()
    {
        SolarCorpus.Growth.ShouldBe(SolarCorpus.Production2025 - SolarCorpus.Production2024);
        (100d * SolarCorpus.Growth / SolarCorpus.Production2024).ShouldBe(45.8, 0.05);
        SolarCorpus.PlannedPower.ShouldBe(SolarCorpus.Power + SolarCorpus.ExpansionPower);
        SolarCorpus.OwnCost.ShouldBe(SolarCorpus.Budget * (100 - SolarCorpus.SubsidyPercent) / 100);
        SolarCorpus.FirstYearCost.ShouldBe(SolarCorpus.EntryFee + SolarCorpus.AnnualFee);
    }

    // The report prints one figure for 2024 and a later page corrects it. Nothing a scenario
    // marks against may come from the figure as printed, or the rung about the correction
    // would be marking the mistake as the answer.
    [Fact]
    public void TheCorrectedYear_IsNotTheOneTheReportPrints_AndTheGrowthFollowsTheCorrection()
    {
        SolarCorpus.Production2024.ShouldNotBe(SolarCorpus.Production2024AsPrinted);
        SolarCorpus.Growth.ShouldNotBe(SolarCorpus.Production2025 - SolarCorpus.Production2024AsPrinted);
    }

    [Fact]
    public void TwoPeopleJoiningToday_PayTheBonusesThatStillApply_AndNotTheCampaignThatEnded()
    {
        // One full entry, one at the young member's half, one annual fee for the household:
        // the welcome discount ended before the day the family is pinned to.
        SolarCorpus.TwoMembersFirstYear.ShouldBe(
            SolarCorpus.EntryFee + SolarCorpus.EntryFee / 2 + SolarCorpus.AnnualFee);
        SolarCorpus.WelcomeEnded.ShouldBeLessThan(DateOnly.FromDateTime(Harness.EvalInstant.Evening.Date));
    }

    [Fact]
    public void TheBonusesPage_IsTwoLinksFromAnySearchResult()
    {
        SolarCorpus.Search("http://site").ShouldNotContain(entry => entry.Url.Contains("/bonificaciones"));
    }

    [Fact]
    public void TheFeesPage_IsInNoSearchResult()
    {
        // Reachable only by a link from a page that is: what makes finding it a matter of
        // reading on from the first hit rather than of asking the right question.
        SolarCorpus.Search("http://site").ShouldNotContain(entry => entry.Url.Contains("/cuotas"));
    }
}