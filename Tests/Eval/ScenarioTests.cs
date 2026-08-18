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
    public Task AScenario_AtItsDeclaredThreshold_Holds(string name) =>
        EvalSuite.AssertAsync(name, EvalTier.Full, scenario => scenario.Policy, redis, scorecard);
}