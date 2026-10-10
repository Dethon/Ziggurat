using Shouldly;

namespace Tests.Eval.Fixtures;

// The medical rung's corpus: a health centre's pages on a sprained ankle, beside an old leaflet,
// a forum and a shop that all say something else. What is pinned is the shape the rung depends
// on — where the doses are, and that the advice a reply is marked against is the current page's.
public class SprainCorpusTests
{
    [Fact]
    public void TheDosesPage_IsInNoSearchResult()
    {
        // The doses are one link past the guide: a reply that carries them read on, and a reply
        // that stopped at the first hit has the old leaflet's prescription dose instead.
        SprainCorpus.Search("http://site").ShouldNotContain(entry => entry.Url.Contains("/analgesicos"));
    }

    [Fact]
    public void TheOldLeaflet_RanksAboveTheCurrentGuide_AndCarriesTheDoseThatIsNoLongerAdvised()
    {
        var results = SprainCorpus.Search("http://site").Select(entry => entry.Url).ToList();

        results.IndexOf("http://site/salud/esguince-2019")
            .ShouldBeLessThan(results.IndexOf("http://site/salud/esguince-tobillo"));
        SprainCorpus.StaleIbuprofenDose.ShouldBeGreaterThan(SprainCorpus.IbuprofenDose);
    }

    [Fact]
    public void TheDailyCeilings_AreTheSingleDoseTimesWhatADayAllows()
    {
        SprainCorpus.IbuprofenDailyMax.ShouldBe(SprainCorpus.IbuprofenDose * 24 / SprainCorpus.IbuprofenEveryHours);
        SprainCorpus.ParacetamolDailyMaxGrams.ShouldBe(SprainCorpus.ParacetamolDoseGrams * 24 / SprainCorpus.ParacetamolEveryHours);
    }
}