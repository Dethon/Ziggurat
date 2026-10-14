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

    // A filmed meal (ticket 10) sends its clip after the photos.
    [Fact]
    public void AFilmedMeal_SendsItsClipAfterThePhotos()
    {
        var turn = SugaryBenchmark.ScenarioOf(_plate with { Clip = "clips/nvr-41.mp4" }, 1).Turn;

        turn.Attachments.Last().ShouldBe(new EvalAttachment(Path.Combine(SugaryCases.Directory, "clips/nvr-41.mp4"), "video/mp4"));
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
            .ShouldNotBeNull().ShouldContain("3 of the benchmark's photos");
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