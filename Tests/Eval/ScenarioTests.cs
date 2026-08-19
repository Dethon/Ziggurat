using System.Reflection;
using Shouldly;
using Tests.Eval.Harness;
using Tests.Eval.Scenarios;
using Tests.Integration.Fixtures;

namespace Tests.Eval;

// The full tier: every scenario at the threshold it declares, leaving a per-claim scorecard
// behind. This is what a maintainer runs before and after a model bump, and the diff between two
// scorecards is what turns "behaviour got worse" into a number.
//
// One class per scenario family rather than one for the suite: xUnit runs a class's cases
// sequentially and parallelizes across classes, so the family classes are what lets ten scenarios
// be in flight at once. Every class fronts the same scorecard ledger, and each leases its own
// Redis database, so the parallelism costs no isolation.

[Trait("Category", "Eval")]
[Trait("Tier", "Full")]
public class TimerScenarioTests(RedisFixture redis, EvalScorecard scorecard)
    : IClassFixture<RedisFixture>, IClassFixture<EvalScorecard>
{
    public static TheoryData<string> Scenarios => EvalSuite.Named(TimerScenarios.All);

    [SkippableTheory]
    [MemberData(nameof(Scenarios))]
    public Task AScenario_AtItsDeclaredThreshold_Holds(string name) =>
        EvalSuite.AssertAsync(name, EvalTier.Full, scenario => scenario.Policy, redis, scorecard);
}

[Trait("Category", "Eval")]
[Trait("Tier", "Full")]
public class MechanismScenarioTests(RedisFixture redis, EvalScorecard scorecard)
    : IClassFixture<RedisFixture>, IClassFixture<EvalScorecard>
{
    public static TheoryData<string> Scenarios => EvalSuite.Named(MechanismScenarios.All);

    [SkippableTheory]
    [MemberData(nameof(Scenarios))]
    public Task AScenario_AtItsDeclaredThreshold_Holds(string name) =>
        EvalSuite.AssertAsync(name, EvalTier.Full, scenario => scenario.Policy, redis, scorecard);
}

[Trait("Category", "Eval")]
[Trait("Tier", "Full")]
public class VoiceScenarioTests(RedisFixture redis, EvalScorecard scorecard)
    : IClassFixture<RedisFixture>, IClassFixture<EvalScorecard>
{
    public static TheoryData<string> Scenarios => EvalSuite.Named(VoiceScenarios.All);

    [SkippableTheory]
    [MemberData(nameof(Scenarios))]
    public Task AScenario_AtItsDeclaredThreshold_Holds(string name) =>
        EvalSuite.AssertAsync(name, EvalTier.Full, scenario => scenario.Policy, redis, scorecard);
}

[Trait("Category", "Eval")]
[Trait("Tier", "Full")]
public class HomeAssistantScenarioTests(RedisFixture redis, EvalScorecard scorecard)
    : IClassFixture<RedisFixture>, IClassFixture<EvalScorecard>
{
    public static TheoryData<string> Scenarios => EvalSuite.Named(HomeAssistantScenarios.All);

    [SkippableTheory]
    [MemberData(nameof(Scenarios))]
    public Task AScenario_AtItsDeclaredThreshold_Holds(string name) =>
        EvalSuite.AssertAsync(name, EvalTier.Full, scenario => scenario.Policy, redis, scorecard);
}

[Trait("Category", "Eval")]
[Trait("Tier", "Full")]
public class VaultScenarioTests(RedisFixture redis, EvalScorecard scorecard)
    : IClassFixture<RedisFixture>, IClassFixture<EvalScorecard>
{
    public static TheoryData<string> Scenarios => EvalSuite.Named(VaultScenarios.All);

    [SkippableTheory]
    [MemberData(nameof(Scenarios))]
    public Task AScenario_AtItsDeclaredThreshold_Holds(string name) =>
        EvalSuite.AssertAsync(name, EvalTier.Full, scenario => scenario.Policy, redis, scorecard);
}

[Trait("Category", "Eval")]
[Trait("Tier", "Full")]
public class MountScenarioTests(RedisFixture redis, EvalScorecard scorecard)
    : IClassFixture<RedisFixture>, IClassFixture<EvalScorecard>
{
    public static TheoryData<string> Scenarios => EvalSuite.Named(MountScenarios.All);

    [SkippableTheory]
    [MemberData(nameof(Scenarios))]
    public Task AScenario_AtItsDeclaredThreshold_Holds(string name) =>
        EvalSuite.AssertAsync(name, EvalTier.Full, scenario => scenario.Policy, redis, scorecard);
}

[Trait("Category", "Eval")]
[Trait("Tier", "Full")]
public class DelegationScenarioTests(RedisFixture redis, EvalScorecard scorecard)
    : IClassFixture<RedisFixture>, IClassFixture<EvalScorecard>
{
    public static TheoryData<string> Scenarios => EvalSuite.Named(DelegationScenarios.All);

    [SkippableTheory]
    [MemberData(nameof(Scenarios))]
    public Task AScenario_AtItsDeclaredThreshold_Holds(string name) =>
        EvalSuite.AssertAsync(name, EvalTier.Full, scenario => scenario.Policy, redis, scorecard);
}

[Trait("Category", "Eval")]
[Trait("Tier", "Full")]
public class MemoryScenarioTests(RedisFixture redis, EvalScorecard scorecard)
    : IClassFixture<RedisFixture>, IClassFixture<EvalScorecard>
{
    public static TheoryData<string> Scenarios => EvalSuite.Named(MemoryScenarios.All);

    [SkippableTheory]
    [MemberData(nameof(Scenarios))]
    public Task AScenario_AtItsDeclaredThreshold_Holds(string name) =>
        EvalSuite.AssertAsync(name, EvalTier.Full, scenario => scenario.Policy, redis, scorecard);
}

[Trait("Category", "Eval")]
[Trait("Tier", "Full")]
public class MusicScenarioTests(RedisFixture redis, EvalScorecard scorecard)
    : IClassFixture<RedisFixture>, IClassFixture<EvalScorecard>
{
    public static TheoryData<string> Scenarios => EvalSuite.Named(MusicScenarios.All);

    [SkippableTheory]
    [MemberData(nameof(Scenarios))]
    public Task AScenario_AtItsDeclaredThreshold_Holds(string name) =>
        EvalSuite.AssertAsync(name, EvalTier.Full, scenario => scenario.Policy, redis, scorecard);
}

[Trait("Category", "Eval")]
[Trait("Tier", "Full")]
public class WebScenarioTests(RedisFixture redis, EvalScorecard scorecard)
    : IClassFixture<RedisFixture>, IClassFixture<EvalScorecard>
{
    public static TheoryData<string> Scenarios => EvalSuite.Named(WebScenarios.All);

    [SkippableTheory]
    [MemberData(nameof(Scenarios))]
    public Task AScenario_AtItsDeclaredThreshold_Holds(string name) =>
        EvalSuite.AssertAsync(name, EvalTier.Full, scenario => scenario.Policy, redis, scorecard);
}

// The seam the split opened: a family added to EvalSuite.All without a class here would report
// its claims as covered while nothing executes them, and a scenario wired into two classes would
// be recorded twice. Deterministic, so a bare run catches both before any money is spent.
public class ScenarioWiringTests
{
    [Fact]
    public void EveryScenario_IsWiredIntoExactlyOneFullTierClass()
    {
        var wired = typeof(ScenarioWiringTests).Assembly.GetTypes()
            .Where(type => !type.IsAbstract && type.CustomAttributes.Any(attribute =>
                attribute.AttributeType == typeof(TraitAttribute)
                && Equals(attribute.ConstructorArguments[0].Value, "Tier")
                && Equals(attribute.ConstructorArguments[1].Value, "Full")))
            .SelectMany(Named)
            .ToList();

        wired.ShouldBe(EvalSuite.All.Select(s => s.Name).ToList(), ignoreOrder: true);
    }

    private static IEnumerable<string> Named(Type type) =>
        ((IEnumerable<object[]>)type
            .GetProperty("Scenarios", BindingFlags.Public | BindingFlags.Static)!
            .GetValue(null)!)
            .Select(row => (string)row[0]);
}