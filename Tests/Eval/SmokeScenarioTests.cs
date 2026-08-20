using Tests.Eval.Harness;

namespace Tests.Eval;

// The smoke tier: one canary per family, once each. What a prompt author runs while editing a
// single section, where a full pass would cost more than the edit is worth.
//
// Split across classes for the same reason the long families are: a canary is one run, so a
// single class would be ten turns end to end — the tier whose whole point is a short wait.
[Trait("Category", "Eval")]
[Trait("Tier", "Smoke")]
public class SmokeScenarioTests(EvalScorecard scorecard) : IClassFixture<EvalScorecard>
{
    public static TheoryData<string> Scenarios => EvalSuite.Named(EvalTier.Smoke, 0, of: 2);

    [SkippableTheory]
    [MemberData(nameof(Scenarios))]
    public Task ACanary_InOneRun_Holds(string name) =>
        EvalSuite.AssertAsync(name, EvalTier.Smoke, _ => RunPolicy.Once, scorecard);
}

[Trait("Category", "Eval")]
[Trait("Tier", "Smoke")]
public class SmokeScenarioTests2(EvalScorecard scorecard) : IClassFixture<EvalScorecard>
{
    public static TheoryData<string> Scenarios => EvalSuite.Named(EvalTier.Smoke, 1, of: 2);

    [SkippableTheory]
    [MemberData(nameof(Scenarios))]
    public Task ACanary_InOneRun_Holds(string name) =>
        EvalSuite.AssertAsync(name, EvalTier.Smoke, _ => RunPolicy.Once, scorecard);
}