using Shouldly;

namespace Tests.Eval.Sugary;

// The benchmark's meals as the tier reads them. Every number on the scorecard is measured against
// these, so a case that lost its truth, its set or its photo on the way in would be scored as a
// different meal without anything failing.
public class SugaryCasesTests
{
    [Fact]
    public void TheShippedPhotoCases_AreTheStandalonesSixtyFourMeals_InTheirThreeSets()
    {
        var cases = SugaryCases.Shipped.Where(c => c.Input == SugaryInput.Photo).ToList();

        cases.Count.ShouldBe(64);
        cases.GroupBy(c => c.Set).ToDictionary(set => set.Key, set => set.Count())
            .ShouldBe(new Dictionary<string, int> { ["n5k"] = 14, ["nvr"] = 18, ["snap"] = 32 }, ignoreOrder: true);
        SugaryCases.Shipped.Select(c => c.Name).Distinct().Count().ShouldBe(SugaryCases.Shipped.Count);
    }

    [Fact]
    public void EveryShippedPhotoCase_CarriesItsTruth_AndAtLeastOnePhoto()
    {
        var cases = SugaryCases.Shipped.Where(c => c.Input == SugaryInput.Photo).ToList();

        cases.ShouldAllBe(c => c.Carbs >= 0 && c.Grams > 0 && c.Photos.Count > 0 && c.Clip == null && c.Stills.Count == 0);
        cases.Where(c => c.Set == "nvr").ShouldAllBe(c => c.Photos.Count == 4);
    }

    // The standalone's video figures were measured on the 14 Nutrition5k dishes filmed by the rig's
    // side cameras, from the clip alone. Each video case is one of the photographed n5k dishes, with
    // its truth, and sends its clip and no photo, so the two inputs are compared on the same meals.
    [Fact]
    public void TheShippedVideoCases_AreTheFourteenNutrition5kDishesAgain_FilmedWithTheirTruth()
    {
        var photographed = SugaryCases.Shipped.Where(c => c is { Set: "n5k", Input: SugaryInput.Photo })
            .ToDictionary(c => Dish(c.Photos[0]));
        var filmed = SugaryCases.Shipped.Where(c => c.Input == SugaryInput.Video).ToList();

        filmed.Count.ShouldBe(14);
        filmed.ShouldAllBe(c => c.Set == "n5k" && c.Photos.Count == 0);
        filmed.Select(c => Dish(c.Clip!)).ShouldBe(photographed.Keys, ignoreOrder: true);
        foreach (var clip in filmed)
        {
            var photo = photographed[Dish(clip.Clip!)];
            (clip.Carbs, clip.TotalCarbs, clip.Grams, clip.Truth).ShouldBe((photo.Carbs, photo.TotalCarbs, photo.Grams, photo.Truth));
            clip.Name.ShouldBe(photo.Name.Replace(":", " (video):"));
            clip.Stills.Count.ShouldBe(Dish(clip.Clip!) == "1558641320" ? 4 : 8, clip.Name);
            clip.Stills.ShouldAllBe(still => still.StartsWith($"clips/n5k-{Dish(clip.Clip!)}-"));
        }
    }

    private static string Dish(string path) => Path.GetFileNameWithoutExtension(path).Split('-')[1];

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
            [{ "name": "n5k 1 (video)", "set": "n5k", "photos": [], "clip": "clips/n5k-1.mp4",
               "stills": ["clips/n5k-1-A1.jpg", "clips/n5k-1-B1.jpg"], "carbs": 41.7, "grams": 336 }]
            """).Single();

        read.Clip.ShouldBe("clips/n5k-1.mp4");
        read.Stills.ShouldBe(["clips/n5k-1-A1.jpg", "clips/n5k-1-B1.jpg"]);
        read.Input.ShouldBe(SugaryInput.Video);
    }

    [Fact]
    public void ACaseWithNoTruth_IsRefusedWhenRead()
    {
        Should.Throw<InvalidDataException>(() => SugaryCases.Parse(
            """[{ "name": "no carbs", "set": "n5k", "photos": ["photos/x.jpg"], "grams": 10 }]"""));
    }
}