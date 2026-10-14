using System.Text.Json.Nodes;
using Domain.Tools.Sugary.Estimation;
using Tests.Eval.Harness;

namespace Tests.Eval.Sugary;

// One benchmark meal, estimated once: the estimate the server would have shown, or why there is
// none. The answer is read by the server's own parse and arithmetic (MealEstimateReply, then
// MealEstimate.From), so what is scored is the total the app would show — the widened ranges and
// the sum of the items — never the model's own figures.
public sealed record SugaryReading
{
    public required SugaryCase Case { get; init; }

    public MealEstimate? Estimate { get; init; }

    public string? Failure { get; init; }

    public TimeSpan Elapsed { get; init; }

    public Spend Spend { get; init; } = Spend.Nothing;

    public int PhotosSent { get; init; }

    public double? Error => Estimate is null ? null : Estimate.Total.Best - Case.Carbs;

    public bool? InRange => Estimate is null
        ? null
        : Case.Carbs >= Estimate.Total.Low && Case.Carbs <= Estimate.Total.High;

    public double? EstimatedGrams => Estimate?.Items.Sum(item => item.Grams.Best);

    public static SugaryReading Of(
        SugaryCase evalCase, string? answer, TimeSpan elapsed, Spend spend, int photosSent, int photosServed)
    {
        var reading = new SugaryReading { Case = evalCase, Elapsed = elapsed, Spend = spend, PhotosSent = photosSent };

        // Checked before the answer is read: an estimate made without its photo is a guess from the
        // sentence announcing one, and scoring it would credit the model with the harness's failure.
        if (photosServed < photosSent)
        {
            return reading with { Failure = $"only {photosServed} of {photosSent} photos reached the model" };
        }

        if (string.IsNullOrWhiteSpace(answer))
        {
            return reading with { Failure = "The model returned no answer." };
        }

        try
        {
            return reading with { Estimate = MealEstimateReply.Read(answer) };
        }
        catch (UnreadableEstimateException e)
        {
            return reading with { Failure = e.Message };
        }
    }

    // A run that ended without an answer to read — out of time, refused by the provider — is a meal
    // like any other on the scorecard, with that reason, and its bill still counts.
    public static SugaryReading Failed(SugaryCase evalCase, string failure, TimeSpan elapsed, Spend spend, int photosSent) =>
        new() { Case = evalCase, Failure = failure, Elapsed = elapsed, Spend = spend, PhotosSent = photosSent };
}

// The figures the standalone benchmark reported, over any group of meals. Accuracy and time are
// taken over the meals that were answered — a failed meal has no error to average — while the bill
// is over every meal, because a failed estimate was paid for too. A figure nothing answered is null
// rather than zero: a mean error of zero over no meals reads as a perfect model.
public sealed record SugarySummary(
    int Meals,
    int Answered,
    double? MeanAbsoluteError,
    double? Bias,
    int InRange,
    double? InRangeRate,
    double? MeanEstimatedGrams,
    double? MeanTrueGrams,
    double? WeightBias,
    double? WeightMeanAbsoluteError,
    double? MedianSeconds,
    decimal Cost,
    decimal CostPerEstimate)
{
    public static SugarySummary Of(IEnumerable<SugaryReading> readings)
    {
        var all = readings.ToList();
        var answered = all.Where(reading => reading.Estimate is not null).ToList();
        var errors = answered.Select(reading => reading.Error!.Value).ToList();
        var weighed = answered.Where(reading => reading.Case.Grams is not null).ToList();
        var weightErrors = weighed.Select(reading => reading.EstimatedGrams!.Value - reading.Case.Grams!.Value).ToList();
        var cost = all.Sum(reading => reading.Spend.Cost);

        return new SugarySummary(
            all.Count,
            answered.Count,
            Mean(errors.Select(Math.Abs)),
            Mean(errors),
            answered.Count(reading => reading.InRange == true),
            answered.Count == 0 ? null : (double)answered.Count(reading => reading.InRange == true) / answered.Count,
            Mean(weighed.Select(reading => reading.EstimatedGrams!.Value)),
            Mean(weighed.Select(reading => reading.Case.Grams!.Value)),
            Mean(weightErrors),
            Mean(weightErrors.Select(Math.Abs)),
            Median(answered.Select(reading => reading.Elapsed.TotalSeconds)),
            cost,
            all.Count == 0 ? 0 : cost / all.Count);
    }

    public JsonObject ToJson() => new()
    {
        ["meals"] = Meals,
        ["answered"] = Answered,
        ["meanAbsoluteError"] = MeanAbsoluteError,
        ["bias"] = Bias,
        ["inRange"] = InRange,
        ["inRangeRate"] = InRangeRate,
        ["meanEstimatedGrams"] = MeanEstimatedGrams,
        ["meanTrueGrams"] = MeanTrueGrams,
        ["weightBias"] = WeightBias,
        ["weightMeanAbsoluteError"] = WeightMeanAbsoluteError,
        ["medianSeconds"] = MedianSeconds,
        ["cost"] = Cost,
        ["costPerEstimate"] = CostPerEstimate
    };

    private static double? Mean(IEnumerable<double> values)
    {
        var list = values.ToList();
        return list.Count == 0 ? null : list.Average();
    }

    private static double? Median(IEnumerable<double> values)
    {
        var sorted = values.Order().ToList();
        return sorted.Count switch
        {
            0 => null,
            var n when n % 2 == 1 => sorted[n / 2],
            var n => (sorted[(n / 2) - 1] + sorted[n / 2]) / 2
        };
    }
}

// The benchmark's part of a scorecard: the summary overall, per set, per input and per set and
// input together, then every meal with what it was estimated at. Sets are kept apart because
// they measure different things (`snap` is agreement with a person, not accuracy), and inputs
// because a gain from video lands on the meals it was filmed for.
public static class SugaryScore
{
    public static JsonObject Json(IReadOnlyList<SugaryReading> readings) => new()
    {
        ["overall"] = SugarySummary.Of(readings).ToJson(),
        ["bySet"] = Grouped(readings, reading => reading.Case.Set, Summarised),
        ["byInput"] = Grouped(readings, Input, Summarised),
        ["bySetAndInput"] = Grouped(readings, reading => reading.Case.Set,
            set => Grouped(set, Input, Summarised)),
        ["meals"] = new JsonArray([.. readings.Select(Row)])
    };

    private static string Input(SugaryReading reading) => reading.Case.Input.ToString().ToLowerInvariant();

    private static JsonNode Summarised(IReadOnlyList<SugaryReading> readings) => SugarySummary.Of(readings).ToJson();

    // In ordinal order, so two scorecards list their groups the same way.
    private static JsonObject Grouped(
        IEnumerable<SugaryReading> readings, Func<SugaryReading, string> key,
        Func<IReadOnlyList<SugaryReading>, JsonNode> summarise) =>
        readings
            .GroupBy(key, StringComparer.Ordinal)
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .Aggregate(new JsonObject(), (node, group) =>
            {
                node[group.Key] = summarise([.. group]);
                return node;
            });

    private static JsonNode Row(SugaryReading reading) => new JsonObject
    {
        ["name"] = reading.Case.Name,
        ["set"] = reading.Case.Set,
        ["input"] = Input(reading),
        ["photos"] = reading.PhotosSent,
        ["carbs"] = reading.Case.Carbs,
        ["estimate"] = reading.Estimate is { } estimate
            ? new JsonObject
            {
                ["low"] = estimate.Total.Low,
                ["best"] = estimate.Total.Best,
                ["high"] = estimate.Total.High
            }
            : null,
        ["error"] = reading.Error,
        ["inRange"] = reading.InRange,
        ["grams"] = reading.Case.Grams,
        ["estimatedGrams"] = reading.EstimatedGrams,
        ["seconds"] = reading.Elapsed.TotalSeconds,
        ["cost"] = reading.Spend.Cost,
        ["failure"] = reading.Failure,
        ["items"] = reading.Estimate is { } items
            ? new JsonArray([
                .. items.Items.Select(item => (JsonNode)new JsonObject
                {
                    ["name"] = item.Name,
                    ["portion"] = item.Portion,
                    ["grams"] = item.Grams.Best,
                    ["carbsPer100g"] = item.CarbsPer100g,
                    ["carbs"] = item.Carbs.Best,
                    ["confidence"] = item.Confidence.ToString().ToLowerInvariant()
                })
            ])
            : null,
        ["questions"] = reading.Estimate is { } asked
            ? new JsonArray([.. asked.Questions.Select(question => (JsonNode)JsonValue.Create(question.Text))])
            : null
    };
}