using Shouldly;

namespace Tests.Eval.Sugary;

// The benchmark's meals as the tier reads them. Every number on the scorecard is measured against
// these, so a case that lost its truth, its set or its photo on the way in would be scored as a
// different meal without anything failing.
public class SugaryCasesTests
{
    [Fact]
    public void TheShippedCases_AreTheStandalonesSixtyFourMeals_InTheirThreeSets()
    {
        var cases = SugaryCases.Shipped;

        cases.Count.ShouldBe(64);
        cases.GroupBy(c => c.Set).ToDictionary(set => set.Key, set => set.Count())
            .ShouldBe(new Dictionary<string, int> { ["n5k"] = 14, ["nvr"] = 18, ["snap"] = 32 }, ignoreOrder: true);
        cases.Select(c => c.Name).Distinct().Count().ShouldBe(64);
    }

    [Fact]
    public void EveryShippedCase_CarriesItsTruth_AndAtLeastOnePhoto()
    {
        SugaryCases.Shipped.ShouldAllBe(c =>
            c.Carbs >= 0 && c.Grams > 0 && c.Photos.Count > 0 && c.Clip == null && c.Input == SugaryInput.Photo);
        SugaryCases.Shipped.Where(c => c.Set == "nvr").ShouldAllBe(c => c.Photos.Count == 4);
    }

    [Fact]
    public void ACase_IsReadFieldForField()
    {
        var read = SugaryCases.Parse(
            """
            [{
              "name": "n5k 1: rice and chicken", "set": "n5k", "photos": ["photos/n5k-1.jpg"],
              "description": null, "hint": "rice and chicken", "carbs": 41.7, "totalCarbs": 49.1,
              "grams": 336, "truth": "rice 110 g; chicken 226 g"
            }]
            """).Single();

        read.Photos.ShouldBe(["photos/n5k-1.jpg"]);
        (read with { Photos = [] }).ShouldBe(new SugaryCase
        {
            Name = "n5k 1: rice and chicken",
            Set = "n5k",
            Photos = [],
            Hint = "rice and chicken",
            Carbs = 41.7,
            TotalCarbs = 49.1,
            Grams = 336,
            Truth = "rice 110 g; chicken 226 g"
        });
    }

    // Ticket 10 films the Nutrition5k dishes again: a case that carries a clip is a video case, and
    // the scorecard keeps it apart from the photographed ones.
    [Fact]
    public void ACaseWithAClip_IsAVideoCase()
    {
        var read = SugaryCases.Parse(
            """
            [{ "name": "n5k 1 (video)", "set": "n5k", "photos": ["photos/n5k-1.jpg"], "clip": "clips/n5k-1.mp4",
               "carbs": 41.7, "grams": 336 }]
            """).Single();

        read.Clip.ShouldBe("clips/n5k-1.mp4");
        read.Input.ShouldBe(SugaryInput.Video);
    }

    [Fact]
    public void ACaseWithNoTruth_IsRefusedWhenRead()
    {
        Should.Throw<InvalidDataException>(() => SugaryCases.Parse(
            """[{ "name": "no carbs", "set": "n5k", "photos": ["photos/x.jpg"], "grams": 10 }]"""));
    }
}