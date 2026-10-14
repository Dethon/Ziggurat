using Domain.Tools.Sugary.Estimation;
using McpServerSugary.Channel;
using Shouldly;
using Tests.Eval.Harness;

namespace Tests.Eval.Sugary;

// How a benchmark meal becomes a turn of the sugary agent, and when the tier runs at all — without
// a model. The turn has to be the one the Sugary channel sends, or the scorecard measures a request
// no deployment makes; and the tier has to stay closed on every run nobody asked it for by model.
public class SugaryBenchmarkTests
{
    private static readonly SugaryCase _plate = new()
    {
        Name = "nvr 41: a chicken leg and a chicken wing",
        Set = "nvr",
        Photos = ["photos/nvr-41a.jpg", "photos/nvr-41b.jpg", "photos/nvr-41c.png", "photos/nvr-41d.webp"],
        Carbs = 0.5,
        Grams = 180
    };

    [Fact]
    public void AMeal_IsTheTurnTheSugaryChannelSends()
    {
        var turn = SugaryBenchmark.ScenarioOf(_plate, photosPerCase: 1).Turn;

        turn.Text.ShouldBe(MealTurn.Text(new MealInput(1, null, null, "en")));
        turn.Text.ShouldStartWith("One photo of the meal is attached.");
        turn.Sender.ShouldBe(SugaryChannel.Sender);
        turn.Channel.ShouldBe("sugary");
        turn.ResponseFormat.ShouldBe(MealEstimateSchema.Format);
        turn.Attachments.ShouldBe([new EvalAttachment(Path.Combine(SugaryCases.Directory, "photos/nvr-41a.jpg"), "image/jpeg")]);
    }

    [Fact]
    public void SeveralPhotos_AreSentInTheirOrder_AndTheTurnCountsThem()
    {
        var turn = SugaryBenchmark.ScenarioOf(_plate, photosPerCase: 4).Turn;

        turn.Text.ShouldStartWith("4 photos of the same meal are attached.");
        turn.Attachments.Select(a => a.MediaType).ShouldBe(["image/jpeg", "image/jpeg", "image/png", "image/webp"]);
        SugaryBenchmark.ScenarioOf(_plate, photosPerCase: 9).Turn.Attachments.Count.ShouldBe(4);
    }

    [Fact]
    public void ADescribedMeal_CarriesItsDescription()
    {
        var turn = SugaryBenchmark.ScenarioOf(_plate with { Description = "two pieces of roast chicken" }, 1).Turn;

        turn.Text.ShouldContain("The person's description:\ntwo pieces of roast chicken");
    }

    // A filmed meal is sent as the Sugary channel sends one: the clip after her photos, naming its
    // stills, and the stills as pictures of the same turn, so the send can give a model that only
    // sees pictures the stills instead (ADR-0045).
    [Fact]
    public void AFilmedMeal_SendsItsClipNamingItsStills_AfterThePhotos()
    {
        var turn = SugaryBenchmark.ScenarioOf(
            _plate with { Clip = "clips/nvr-41.mp4", Stills = ["clips/nvr-41-A1.jpg", "clips/nvr-41-B1.jpg"] }, 1).Turn;

        turn.Text.ShouldBe(MealTurn.Text(new MealInput(1, null, null, "en", Filmed: true)));
        turn.Attachments.Select(a => (Path.GetRelativePath(SugaryCases.Directory, a.Path), a.MediaType)).ShouldBe(
        [
            ("photos/nvr-41a.jpg", "image/jpeg"), ("clips/nvr-41.mp4", "video/mp4"),
            ("clips/nvr-41-A1.jpg", "image/jpeg"), ("clips/nvr-41-B1.jpg", "image/jpeg")
        ]);
        turn.Attachments[1].Stills.ShouldBe([turn.Attachments[2].Path, turn.Attachments[3].Path]);
    }

    // The standalone's video figures were measured from the clip alone, with no photo beside it.
    [Fact]
    public void AMealFilmedAndNotPhotographed_SaysItIsAVideo_NeverAMealWithNoPhoto()
    {
        var turn = SugaryBenchmark.ScenarioOf(_plate with { Photos = [], Clip = "clips/n5k-1.mp4" }, 1).Turn;

        turn.Text.ShouldStartWith("A video of the meal is attached, filmed while moving around the plate.");
        turn.Attachments.ShouldHaveSingleItem().MediaType.ShouldBe("video/mp4");
    }

    [Fact]
    public void AMealsScenario_IsTheSugaryAgentsInTheSugaryTier_GroupedByItsSet()
    {
        var scenario = SugaryBenchmark.ScenarioOf(_plate, 1);

        scenario.AgentId.ShouldBe(SugaryBenchmark.Agent);
        scenario.Tier.ShouldBe(EvalTier.Sugary);
        scenario.Group.ShouldBe("nvr");
        scenario.Name.ShouldContain(_plate.Name);
    }

    [Theory]
    [InlineData(null, 1)]
    [InlineData("", 1)]
    [InlineData("4", 4)]
    [InlineData("0", 1)]
    [InlineData("many", 1)]
    public void PhotosPerMeal_IsOneUnlessAskedFor(string? variable, int expected) =>
        SugaryBenchmark.PhotosPerCase(variable).ShouldBe(expected);

    [Fact]
    public void ABareRun_IsNotTheTier()
    {
        SugaryBenchmark.WhyNot(armed: false, apiKey: "k", model: "m", missingPhotos: 0)
            .ShouldNotBeNull().ShouldContain("ZIGGURAT_EVAL=1");
    }

    [Fact]
    public void AnArmedRunWithNoModelNamed_IsNotTheTier()
    {
        // The tier compares models, so a run that names none has nothing to file its scorecard as.
        SugaryBenchmark.WhyNot(armed: true, apiKey: "k", model: null, missingPhotos: 0)
            .ShouldNotBeNull().ShouldContain("ZIGGURAT_EVAL_MODEL");
        SugaryBenchmark.WhyNot(armed: true, apiKey: "k", model: " ", missingPhotos: 0)
            .ShouldNotBeNull().ShouldContain("ZIGGURAT_EVAL_MODEL");
    }

    [Fact]
    public void ARunWithoutAKey_OrWithoutThePhotos_IsNotTheTier()
    {
        SugaryBenchmark.WhyNot(armed: true, apiKey: null, model: "m", missingPhotos: 0)
            .ShouldNotBeNull().ShouldContain("openRouter:apiKey");
        SugaryBenchmark.WhyNot(armed: true, apiKey: "k", model: "m", missingPhotos: 3)
            .ShouldNotBeNull().ShouldContain("3 of the benchmark's photos and clips");
    }

    [Fact]
    public void AnArmedRun_WithAKeyAModelAndThePhotos_IsTheTier() =>
        SugaryBenchmark.WhyNot(armed: true, apiKey: "k", model: "google/gemini-3.8-flash", missingPhotos: 0)
            .ShouldBeNull();

    [Fact]
    public void MissingPhotos_AreCountedOverWhatTheRunWouldSend()
    {
        var cases = new[] { _plate with { Photos = [$"photos/{Guid.NewGuid():N}.jpg", "photos/also-missing.jpg"] } };

        SugaryBenchmark.MissingPhotos(cases, photosPerCase: 1).ShouldBe(1);
        SugaryBenchmark.MissingPhotos(cases, photosPerCase: 2).ShouldBe(2);
    }

    // A filmed meal whose clip or stills are not on disk would be estimated from less than it says.
    [Fact]
    public void AMissingClipOrStill_CountsAsMissing()
    {
        var cases = new[] { _plate with { Photos = [], Clip = "clips/missing.mp4", Stills = ["clips/missing-A1.jpg"] } };

        SugaryBenchmark.MissingPhotos(cases, photosPerCase: 1).ShouldBe(2);
    }

    [Fact]
    public void ARunThatRanOutOfTime_OrWasRefused_IsAFailedMeal()
    {
        var scenario = SugaryBenchmark.ScenarioOf(_plate, 1);

        SugaryBenchmark.Read(_plate, scenario, new Recording { TimedOut = true, Elapsed = scenario.Budget })
            .Failure.ShouldBe($"the turn never finished within {scenario.Budget.TotalSeconds:0}s");
        SugaryBenchmark.Read(_plate, scenario, new Recording { ProviderError = "502 Bad Gateway" })
            .Failure.ShouldBe("the provider refused the turn: 502 Bad Gateway");
    }

    [Fact]
    public void AnAnsweredRun_IsReadByTheServersParse_OverThePhotosItWasSent()
    {
        var scenario = SugaryBenchmark.ScenarioOf(_plate, 1);

        var reading = SugaryBenchmark.Read(_plate, scenario, new Recording
        {
            Answer = """{"items":[],"questions":[],"notes":["no food"]}""",
            AttachmentsServed = ["eval-attachment-1"],
            Elapsed = TimeSpan.FromSeconds(7)
        });

        reading.Failure.ShouldBeNull();
        reading.Estimate!.Notes.ShouldBe(["no food"]);
        reading.Elapsed.ShouldBe(TimeSpan.FromSeconds(7));
        SugaryBenchmark.Read(_plate, scenario, new Recording { Answer = "{}" }).Failure
            .ShouldBe("only 0 of 1 photos reached the model");
    }

    // A filmed meal reaches a model as its clip or as its stills, never both (ADR-0045), so it was
    // seen whole when every photo was served and either the clip or every still was.
    [Theory]
    [InlineData("the clip, on a model that watches video", new[] { "eval-attachment-1", "eval-attachment-2" }, null)]
    [InlineData("the stills, on a model that only sees pictures",
        new[] { "eval-attachment-1", "eval-attachment-3", "eval-attachment-4" }, null)]
    [InlineData("one still short", new[] { "eval-attachment-1", "eval-attachment-3" },
        "only 1 of 2 photos reached the model")]
    [InlineData("the clip but not the photo", new[] { "eval-attachment-2" }, "only 1 of 2 photos reached the model")]
    public void AFilmedMeal_WasSeenWhole_WhenItsPhotosAndEitherItsClipOrEveryStillWereServed(
        string what, string[] served, string? failure)
    {
        var filmed = _plate with { Clip = "clips/nvr-41.mp4", Stills = ["clips/nvr-41-A1.jpg", "clips/nvr-41-B1.jpg"] };
        var scenario = SugaryBenchmark.ScenarioOf(filmed, 1);

        SugaryBenchmark.Read(filmed, scenario, new Recording
        {
            Answer = """{"items":[],"questions":[],"notes":[]}""",
            AttachmentsServed = served
        }).Failure.ShouldBe(failure, what);
    }

    // The tier runs only when its filter asks for it: never in the full tier, the smoke tier or
    // the changed tier, and so never on a routine pass.
    [Fact]
    public void TheBenchmark_IsInTheSugaryTierAlone()
    {
        var tiers = typeof(SugaryBenchmarkEvalTests).CustomAttributes
            .Where(attribute => attribute.AttributeType == typeof(TraitAttribute))
            .Select(attribute => ((string)attribute.ConstructorArguments[0].Value!, (string)attribute.ConstructorArguments[1].Value!))
            .ToList();

        tiers.ShouldBe([("Category", "Eval"), ("Tier", "Sugary")], ignoreOrder: true);
    }
}
// A paid pass is the scorecard, so one meal's run throwing — a refusal at the send, a harness
// fault — costs that meal, never the 63 readings beside it.
public class SugaryBenchmarkRunTests
{
    private static readonly SugaryCase[] _meals =
    [
        new() { Name = "n5k 1: rice", Set = "n5k", Photos = ["photos/n5k-1.jpg"], Carbs = 20, Grams = 200 },
        new() { Name = "n5k 2: pasta", Set = "n5k", Photos = ["photos/n5k-2.jpg"], Carbs = 40, Grams = 300 }
    ];

    [Fact]
    public async Task AMealWhoseRunThrows_IsAFailedMeal_AndTheOthersAreRead()
    {
        var pass = await SugaryBenchmark.RunAsync(_meals, 1, scenario =>
            scenario.Name.Contains("pasta")
                ? throw new InvalidOperationException("refused at the send")
                : Task.FromResult(new Recording
                {
                    Answer = """{"items":[],"questions":[],"notes":[]}""",
                    AttachmentsServed = ["eval-attachment-1"]
                }));

        pass.Readings.Select(reading => reading.Case.Name).ShouldBe(["n5k 1: rice", "n5k 2: pasta"]);
        pass.Readings[0].Failure.ShouldBeNull();
        pass.Readings[1].Failure.ShouldBe("the run threw InvalidOperationException: refused at the send");
    }
}