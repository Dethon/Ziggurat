using Shouldly;

namespace Tests.Eval.Fixtures;

// The diabetes rung's corpus: a diabetes unit's sick-day plan, beside an old sheet, a forum and a
// shop that would each have the person do something the plan warns against. The rung marks a
// reply against a dose worked out from the plan's table and the person's own numbers, so the
// numbers have to agree — and the person in the turn has to be somebody the plan keeps at home.
public class SickDayCorpusTests
{
    [Fact]
    public void TheDoseTable_IsInNoSearchResult()
    {
        // One link past the plan. A reply carrying the extra dose read on; a reply that stopped
        // at the first hit has the old sheet telling it to halve the insulin instead.
        SickDayCorpus.Search("http://site").ShouldNotContain(entry => entry.Url.Contains("/cetonas-dosis"));
    }

    [Fact]
    public void TheOldSheet_RanksAboveTheCurrentPlan()
    {
        var results = SickDayCorpus.Search("http://site").Select(entry => entry.Url).ToList();

        results.IndexOf("http://site/diabetes/hoja-2017")
            .ShouldBeLessThan(results.IndexOf("http://site/diabetes/dias-de-enfermedad"));
    }

    [Fact]
    public void ThePersonInTheTurn_IsInTheBandThePlanTreatsAtHome()
    {
        // Above the band that needs nothing and under the one that is an emergency: the right
        // answer is a plan, and "go to hospital now" is as wrong as "wait and see".
        SickDayCorpus.UserKetones.ShouldBeGreaterThanOrEqualTo(SickDayCorpus.ModerateKetones);
        SickDayCorpus.UserKetones.ShouldBeLessThan(SickDayCorpus.EmergencyKetones);
        // Over the level at which the fluids stop being sugary.
        SickDayCorpus.UserGlucose.ShouldBeGreaterThan(SickDayCorpus.SugaryFluidsBelow);
    }

    [Fact]
    public void TheExtraDose_IsTheTablesShareOfThePersonsDailyTotal()
    {
        SickDayCorpus.ExtraDose.ShouldBe(SickDayCorpus.UserDailyTotal * SickDayCorpus.ModeratePercent / 100);
        // A whole number of units: a dose that came out at 3.6 would have every reply rounding
        // its own way and the count marking the rounding.
        (SickDayCorpus.UserDailyTotal * SickDayCorpus.ModeratePercent % 100).ShouldBe(0);
    }
}