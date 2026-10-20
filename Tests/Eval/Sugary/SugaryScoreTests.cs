using System.Text.Json.Nodes;
using Domain.DTOs.Metrics;
using Shouldly;
using Tests.Eval.Harness;

namespace Tests.Eval.Sugary;

// The benchmark's arithmetic, against numbers worked out by hand. A scorecard is read as a
// comparison between two models, so a mean taken over the wrong rows or a range read the wrong
// way round would move both models' figures and still look like a result.
public class SugaryScoreTests
{
    private static readonly SugaryCase _rice = Case("n5k 1: rice", "n5k", carbs: 20, grams: 200);
    private static readonly SugaryCase _pasta = Case("n5k 2: pasta", "n5k", carbs: 40, grams: 300);
    private static readonly SugaryCase _toast = Case("snap 3: toast", "snap", carbs: 30, grams: 90);

    // 10–25–30 g of carbs over 180 g of food: five over the truth of 20, inside the range, 20 g light.
    private static SugaryReading RiceRead() => SugaryReading.Of(
        _rice, Reply((40, 100, 120, 25), (80, 80, 80, 0)), TimeSpan.FromSeconds(10), Paid(0.01m), 1, 1);

    // 20–30–35 g over 330 g: ten under the truth of 40, outside the range, 30 g heavy.
    private static SugaryReading PastaRead() => SugaryReading.Of(
        _pasta, Reply((100, 150, 175, 20), (180, 180, 180, 0)), TimeSpan.FromSeconds(20), Paid(0.02m), 1, 1);

    [Fact]
    public void AnAnsweredMeal_IsScoredOnTheTotalTheServerComputes()
    {
        var reading = RiceRead();

        reading.Failure.ShouldBeNull();
        reading.Estimate!.Total.ShouldBe(new Domain.Tools.Sugary.Estimation.Interval(10, 25, 30));
        reading.Error.ShouldBe(5);
        reading.InRange.ShouldBe(true);
        reading.EstimatedGrams.ShouldBe(180);
        PastaRead().Error.ShouldBe(-10);
        PastaRead().InRange.ShouldBe(false);
    }

    // The truth on either edge of the range is inside it: a range that ends at the true value held it.
    [Theory]
    [InlineData(10, true)]
    [InlineData(30, true)]
    [InlineData(9.9, false)]
    [InlineData(30.1, false)]
    public void TheRange_HoldsTheTruthOnItsEdges(double truth, bool inside)
    {
        SugaryReading.Of(_rice with { Carbs = truth }, Reply((40, 100, 120, 25)), TimeSpan.Zero, Spend.Nothing, 1, 1)
            .InRange.ShouldBe(inside);
    }

    // An eyeballed portion is widened by the server before it is shown, so the range scored is
    // the widened one, never the model's own: 100 g at medium confidence becomes 60–160 g.
    [Fact]
    public void AnEyeballedPortion_IsScoredOnTheWidenedRange()
    {
        var reading = SugaryReading.Of(
            _rice with { Carbs = 15 }, Reply("medium", (95, 100, 105, 10)), TimeSpan.Zero, Spend.Nothing, 1, 1);

        reading.Estimate!.Total.ShouldBe(new Domain.Tools.Sugary.Estimation.Interval(6, 10, 16));
        reading.InRange.ShouldBe(true);
    }

    [Theory]
    [InlineData("I think about 40 g of carbs.")]
    [InlineData("""{"items": [], "questions": []}""")]
    public void AnUnreadableAnswer_IsAFailure_NeverAScore(string reply)
    {
        var reading = SugaryReading.Of(_rice, reply, TimeSpan.FromSeconds(3), Paid(0.01m), 1, 1);

        reading.Failure.ShouldBe("The model returned an answer that could not be read.");
        reading.Estimate.ShouldBeNull();
        reading.Error.ShouldBeNull();
        reading.InRange.ShouldBeNull();
    }

    [Fact]
    public void NoAnswer_IsAFailure()
    {
        SugaryReading.Of(_rice, null, TimeSpan.Zero, Spend.Nothing, 1, 1)
            .Failure.ShouldBe("The model returned no answer.");
        SugaryReading.Of(_rice, "  ", TimeSpan.Zero, Spend.Nothing, 1, 1)
            .Failure.ShouldBe("The model returned no answer.");
    }

    // A photo that never reached the model makes the estimate a guess from the words "One photo
    // of the meal is attached": the run measured the harness, not the model, and says so.
    [Fact]
    public void APhotoThatDidNotReachTheModel_VoidsTheMeal()
    {
        var reading = SugaryReading.Of(
            _rice, Reply((40, 100, 120, 25)), TimeSpan.Zero, Spend.Nothing, photosSent: 1, photosServed: 0);

        reading.Failure.ShouldBe("only 0 of 1 photos reached the model");
        reading.Error.ShouldBeNull();
    }

    [Fact]
    public void TheRunsOwnFailure_IsKept()
    {
        SugaryReading.Failed(_rice, "the turn never finished within 180s", TimeSpan.FromSeconds(180), Paid(0.01m), 1)
            .Failure.ShouldBe("the turn never finished within 180s");
    }

    [Fact]
    public void TheSummary_IsTakenOverTheAnsweredMeals_AndTheBillOverEveryMeal()
    {
        var failed = SugaryReading.Of(_toast, "prose", TimeSpan.FromSeconds(5), Paid(0.005m), 1, 1);

        var summary = SugarySummary.Of([RiceRead(), PastaRead(), failed]);

        summary.Meals.ShouldBe(3);
        summary.Answered.ShouldBe(2);
        summary.MeanAbsoluteError!.Value.ShouldBe(7.5, 1e-9);
        summary.Bias!.Value.ShouldBe(-2.5, 1e-9);
        summary.InRange.ShouldBe(1);
        summary.InRangeRate!.Value.ShouldBe(0.5, 1e-9);
        summary.MeanEstimatedGrams!.Value.ShouldBe(255, 1e-9);
        summary.MeanTrueGrams!.Value.ShouldBe(250, 1e-9);
        summary.WeightBias!.Value.ShouldBe(5, 1e-9);
        summary.WeightMeanAbsoluteError!.Value.ShouldBe(25, 1e-9);
        summary.MedianSeconds!.Value.ShouldBe(15, 1e-9);
        summary.Cost.ShouldBe(0.035m);
        summary.CostPerEstimate.ShouldBe(0.035m / 3);
    }

    [Fact]
    public void ASummaryOfNothingAnswered_SaysNothing_RatherThanZero()
    {
        var summary = SugarySummary.Of([SugaryReading.Of(_rice, null, TimeSpan.Zero, Spend.Nothing, 1, 1)]);

        summary.Answered.ShouldBe(0);
        summary.MeanAbsoluteError.ShouldBeNull();
        summary.InRangeRate.ShouldBeNull();
        summary.MedianSeconds.ShouldBeNull();
    }

    [Fact]
    public void TheMedianTime_OfAnOddCount_IsTheMiddleOne()
    {
        var readings = new[] { 4, 30, 9 }.Select(seconds => SugaryReading.Of(
            _rice, Reply((40, 100, 120, 25)), TimeSpan.FromSeconds(seconds), Spend.Nothing, 1, 1));

        SugarySummary.Of(readings).MedianSeconds.ShouldBe(9);
    }

    [Fact]
    public void TheScorecard_IsPerSet_PerInput_AndOverall_WithEveryMealListed()
    {
        var filmed = SugaryReading.Of(
            Case("n5k 1 (video): rice", "n5k", carbs: 20, grams: 200) with { Clip = "clips/n5k-1.mp4" },
            Reply((40, 100, 120, 20)), TimeSpan.FromSeconds(8), Paid(0.01m), 1, 1);

        var json = SugaryScore.Json([RiceRead(), PastaRead(), filmed]);

        json["overall"]!["meals"]!.GetValue<int>().ShouldBe(3);
        json["bySet"]!.AsObject().Select(set => set.Key).ShouldBe(["n5k"]);
        json["bySet"]!["n5k"]!["meals"]!.GetValue<int>().ShouldBe(3);
        json["byInput"]!.AsObject().Select(input => input.Key).ShouldBe(["photo", "video"]);
        json["byInput"]!["photo"]!["meanAbsoluteError"]!.GetValue<double>().ShouldBe(7.5, 1e-9);
        json["byInput"]!["video"]!["bias"]!.GetValue<double>().ShouldBe(0, 1e-9);
        // Set by input, so a gain from video on one set cannot hide inside another.
        json["bySetAndInput"]!["n5k"]!["video"]!["meals"]!.GetValue<int>().ShouldBe(1);

        var meals = json["meals"]!.AsArray();
        meals.Count.ShouldBe(3);
        var rice = meals[0]!;
        rice["name"]!.GetValue<string>().ShouldBe("n5k 1: rice");
        rice["set"]!.GetValue<string>().ShouldBe("n5k");
        rice["input"]!.GetValue<string>().ShouldBe("photo");
        rice["carbs"]!.GetValue<double>().ShouldBe(20);
        rice["estimate"]!["best"]!.GetValue<double>().ShouldBe(25);
        rice["error"]!.GetValue<double>().ShouldBe(5);
        rice["inRange"]!.GetValue<bool>().ShouldBeTrue();
        rice["grams"]!.GetValue<double>().ShouldBe(200);
        rice["estimatedGrams"]!.GetValue<double>().ShouldBe(180);
        rice["seconds"]!.GetValue<double>().ShouldBe(10);
        rice["cost"]!.GetValue<decimal>().ShouldBe(0.01m);
        // The items themselves, to tell a misjudged portion from a wrong carb density.
        rice["items"]!.AsArray().Count.ShouldBe(2);
        rice["items"]![0]!["grams"]!.GetValue<double>().ShouldBe(100);
        rice["items"]![0]!["carbsPer100g"]!.GetValue<double>().ShouldBe(25);
    }

    [Fact]
    public void AFailedMeal_IsListedWithItsReason()
    {
        var json = SugaryScore.Json([SugaryReading.Of(_rice, "prose", TimeSpan.Zero, Spend.Nothing, 1, 1)]);

        json["meals"]![0]!["failure"]!.GetValue<string>().ShouldBe("The model returned an answer that could not be read.");
        json["meals"]![0]!["error"].ShouldBeNull();
        json["overall"]!["answered"]!.GetValue<int>().ShouldBe(0);
    }

    private static SugaryCase Case(string name, string set, double carbs, double grams) => new()
    {
        Name = name,
        Set = set,
        Photos = [$"photos/{name}.jpg"],
        Carbs = carbs,
        Grams = grams
    };

    private static Spend Paid(decimal cost) =>
        Spend.Of(new TokenUsageEvent { Sender = "Sugary", Model = "google/gemini-3.8-flash", Cost = cost, InputTokens = 1000, OutputTokens = 500 });

    private static string Reply(params (double Low, double Best, double High, double Per100)[] items) =>
        Reply("high", items);

    private static string Reply(string confidence, params (double Low, double Best, double High, double Per100)[] items) =>
        new JsonObject
        {
            ["items"] = new JsonArray([
                .. items.Select((item, index) => (JsonNode)new JsonObject
                {
                    ["name"] = $"item {index + 1}",
                    ["portion"] = "a plate",
                    ["gramsLow"] = item.Low,
                    ["grams"] = item.Best,
                    ["gramsHigh"] = item.High,
                    ["carbsPer100g"] = item.Per100,
                    ["confidence"] = confidence,
                    ["note"] = null
                })
            ]),
            ["questions"] = new JsonArray(),
            ["notes"] = new JsonArray()
        }.ToJsonString();
}