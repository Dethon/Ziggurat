using Domain.Tools.Sugary.Estimation;
using Shouldly;

namespace Tests.Unit.Domain.Sugary;

// The standalone app's estimate tests, carried over as they were: the carb figures are derived in
// code from what the model said about each item, never asked of it.
public class MealEstimateTests
{
    private static ModelItem Item(
        string name = "rice", double low = 100, double best = 150, double high = 200, double density = 28,
        Confidence confidence = Confidence.High) =>
        new(name, "a cup", low, best, high, density, confidence, null);

    private static MealEstimate From(params ModelItem[] items) => MealEstimate.From(new ModelEstimate(items, [], []));

    [Fact]
    public void Carbs_AreDerivedFromGramsAndDensity()
    {
        var item = From(Item()).Items.Single();

        item.Carbs.ShouldBe(new Interval(28, 42, 56));
    }

    [Fact]
    public void Total_IsTheSumOfTheItems()
    {
        var estimate = From(Item(), Item("bread", 30, 40, 50, 50));

        estimate.Total.ShouldBe(new Interval(43, 62, 81));
    }

    [Fact]
    public void Grams_AreReorderedWhenTheModelSwapsTheBounds()
    {
        var item = From(Item(low: 200, best: 100, high: 150)).Items.Single();

        item.Grams.ShouldBe(new Interval(100, 150, 200));
    }

    [Theory]
    [InlineData(Confidence.Medium)]
    [InlineData(Confidence.Low)]
    public void APortionJudgedByEye_KeepsAtLeastTheRangeSuchJudgementsDeserve(Confidence confidence)
    {
        var item = From(Item(low: 140, best: 150, high: 160, confidence: confidence)).Items.Single();

        item.Grams.ShouldBe(new Interval(90, 150, 240));
    }

    [Fact]
    public void APortionJudgedByEye_KeepsAWiderRangeTheModelGave()
    {
        var item = From(Item(low: 50, best: 150, high: 300, confidence: Confidence.Low)).Items.Single();

        item.Grams.ShouldBe(new Interval(50, 150, 300));
    }

    [Fact]
    public void AStatedPortion_KeepsTheRangeTheModelGave()
    {
        var item = From(Item(low: 150, best: 150, high: 150, confidence: Confidence.High)).Items.Single();

        item.Grams.ShouldBe(new Interval(150, 150, 150));
    }

    [Fact]
    public void ImpossibleValues_AreClamped()
    {
        var item = From(Item(low: -20, best: 50, high: 80, density: 140)).Items.Single();

        item.Grams.Low.ShouldBe(0);
        item.CarbsPer100g.ShouldBe(100);
    }

    [Fact]
    public void BlankItemsQuestionsAndNotes_AreDropped()
    {
        var estimate = MealEstimate.From(new ModelEstimate(
            [Item(), Item(name: "  ", low: 0, best: 0, high: 0)],
            [new ModelQuestion("Sugar-free?", ["Yes", "No"]), new ModelQuestion(" ", [])],
            ["High in fat", ""]));

        estimate.Items.Count.ShouldBe(1);
        estimate.Questions.Single().Options.ShouldBe(["Yes", "No"]);
        estimate.Notes.ShouldBe(["High in fat"]);
    }

    // An item with carbs and no name cannot be shown, and dropping it would drop its carbs from the
    // total unseen: the reply is not read at all.
    [Theory]
    [InlineData(150, 150, 150)]
    [InlineData(0, 0, 50)]
    public void ABlankItemCarryingCarbs_RefusesTheReply(double low, double best, double high)
    {
        Should.Throw<UnreadableEstimateException>(() => From(Item(), Item(name: " ", low: low, best: best, high: high)));
    }

    [Fact]
    public void NoItems_GivesAZeroTotal()
    {
        MealEstimate.From(new ModelEstimate([], [], [])).Total.ShouldBe(Interval.Zero);
    }
}