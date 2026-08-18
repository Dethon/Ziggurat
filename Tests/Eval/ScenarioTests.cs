using Shouldly;
using Tests.Eval.Harness;
using Tests.Integration.Fixtures;

namespace Tests.Eval;

// The full tier: every scenario at the threshold it declares, leaving a per-claim scorecard
// behind. This is what a maintainer runs before and after a model bump, and the diff between two
// scorecards is what turns "behaviour got worse" into a number.
[Trait("Category", "Eval")]
[Trait("Tier", "Full")]
public class ScenarioTests(RedisFixture redis, EvalScorecard scorecard)
    : IClassFixture<RedisFixture>, IClassFixture<EvalScorecard>
{
    public static TheoryData<string> Scenarios => EvalSuite.Named(EvalTier.Full);

    [SkippableTheory]
    [MemberData(nameof(Scenarios))]
    public async Task Scenario(string name)
    {
        Skip.IfNot(EvalGate.IsArmed, EvalGate.Reason);
        Skip.If(EvalGate.ApiKey is null, "openRouter:apiKey is not set in user secrets");

        var scenario = EvalSuite.ByName(name);
        var outcome = await EvalSuite.RunAsync(scenario, scenario.Policy, redis);

        scorecard.Record(EvalTier.Full, scenario, outcome.Result);
        scorecard.Observe(outcome.Route);

        outcome.Result.Passed.ShouldBeTrue(
            outcome.Failure ?? $"'{name}' failed with no dump written");
    }
}

// The smoke tier: one canary per family, once each. What a prompt author runs while editing a
// single section, where a full pass would cost more than the edit is worth.
[Trait("Category", "Eval")]
[Trait("Tier", "Smoke")]
public class SmokeScenarioTests(RedisFixture redis, EvalScorecard scorecard)
    : IClassFixture<RedisFixture>, IClassFixture<EvalScorecard>
{
    public static TheoryData<string> Scenarios => EvalSuite.Named(EvalTier.Smoke);

    [SkippableTheory]
    [MemberData(nameof(Scenarios))]
    public async Task Scenario(string name)
    {
        Skip.IfNot(EvalGate.IsArmed, EvalGate.Reason);
        Skip.If(EvalGate.ApiKey is null, "openRouter:apiKey is not set in user secrets");

        var scenario = EvalSuite.ByName(name);
        var outcome = await EvalSuite.RunAsync(scenario, RunPolicy.Once, redis);

        scorecard.Record(EvalTier.Smoke, scenario, outcome.Result);
        scorecard.Observe(outcome.Route);

        outcome.Result.Passed.ShouldBeTrue(
            outcome.Failure ?? $"'{name}' failed with no dump written");
    }
}