using Tests.Eval.Harness;
using Tests.Integration.Fixtures;

namespace Tests.Eval;

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
    public Task ACanary_InOneRun_Holds(string name) =>
        EvalSuite.AssertAsync(name, EvalTier.Smoke, _ => RunPolicy.Once, redis, scorecard);
}