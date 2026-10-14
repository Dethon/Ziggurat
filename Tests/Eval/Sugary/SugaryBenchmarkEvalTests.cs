using Shouldly;
using Tests.Eval.Fixtures;

namespace Tests.Eval.Sugary;

// The Sugary tier's benchmark: the shipped sugary agent, on the model the pass names, estimating
// the 64 benchmark meals from their photos, each scored against its known carbs. Never part of a
// full, smoke or changed pass, and never run without a model named:
//
//   ZIGGURAT_EVAL=1 ZIGGURAT_EVAL_MODEL=<openrouter id> dotnet test --filter "Category=Eval&Tier=Sugary"
//
// The scorecard is filed as `.eval-output/scorecard-sugary-<model>.json`, beside the sugary
// agent's scenarios run on the same model, so two models are two files.
[Trait("Category", "Eval")]
[Trait("Tier", "Sugary")]
public class SugaryBenchmarkEvalTests(EvalScorecard scorecard) : IClassFixture<EvalScorecard>
{
    [SkippableFact]
    public async Task TheBenchmarkMeals_OnTheNamedModel_AreEstimatedAndScored()
    {
        var photos = SugaryBenchmark.PhotosPerCase(Environment.GetEnvironmentVariable(SugaryBenchmark.PhotosVariable));
        // The photos are looked for only once the gate is open, so a bare run touches nothing.
        var missing = EvalGate.IsArmed ? SugaryBenchmark.MissingPhotos(SugaryCases.Shipped, photos) : 0;
        var closed = SugaryBenchmark.WhyNot(
            EvalGate.IsArmed, EvalGate.ApiKey, Environment.GetEnvironmentVariable(EvalModel.Variable), missing);
        Skip.If(closed is not null, closed);

        var pass = await SugaryBenchmark.RunAsync(SugaryCases.Shipped, photos);

        scorecard.RecordBenchmark(pass.Readings);
        scorecard.Observe(pass.Route);
        scorecard.ObservePreload(pass.PreloadModel);

        // The figures are the result and land on the scorecard whatever they are; what fails the
        // pass is a meal that produced no figure — no answer, an unreadable one, or a photo that
        // never reached the model — because a mean over fewer meals is not the benchmark's.
        var failed = pass.Readings.Where(reading => reading.Failure is not null).ToList();
        failed.ShouldBeEmpty(
            $"{failed.Count} of {pass.Readings.Count} meals have no estimate: "
            + string.Join("; ", failed.Select(reading => $"{reading.Case.Name}: {reading.Failure}")));
    }
}