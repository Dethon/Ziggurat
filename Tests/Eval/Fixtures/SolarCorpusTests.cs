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
        (100d * SolarCorpus.Growth / SolarCorpus.Production2024).ShouldBe(50.8, 0.05);
        SolarCorpus.PlannedPower.ShouldBe(SolarCorpus.Power + SolarCorpus.ExpansionPower);
        SolarCorpus.OwnCost.ShouldBe(SolarCorpus.Budget * (100 - SolarCorpus.SubsidyPercent) / 100);
        SolarCorpus.FirstYearCost.ShouldBe(SolarCorpus.EntryFee + SolarCorpus.AnnualFee);
    }

    [Fact]
    public void TheFeesPage_IsInNoSearchResult()
    {
        // Reachable only by a link from a page that is: what makes finding it a matter of
        // reading on from the first hit rather than of asking the right question.
        SolarCorpus.Search("http://site").ShouldNotContain(entry => entry.Url.Contains("/cuotas"));
    }
}