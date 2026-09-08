using System.Reflection;
using Shouldly;
using Tests.Eval.Harness;
using Tests.Eval.Scenarios;

namespace Tests.Eval;

// The full tier: every scenario at the threshold it declares, leaving a per-claim scorecard
// behind. This is what a maintainer runs before and after a model bump, and the diff between two
// scorecards is what turns "behaviour got worse" into a number.
//
// One class per scenario family rather than one for the suite: xUnit runs a class's cases
// sequentially and parallelizes across classes, so the family classes are what lets ten scenarios
// be in flight at once — and each of those puts its own k of N runs out together. Every class
// fronts the same scorecard ledger, every run leases a Redis database of its own, and
// EvalConcurrency bounds how many stacks the two kinds of parallelism can stand up at once.

[Trait("Category", "Eval")]
[Trait("Tier", "Full")]
public class TimerScenarioTests(EvalScorecard scorecard) : IClassFixture<EvalScorecard>
{
    public static TheoryData<string> Scenarios => EvalSuite.Named(TimerScenarios.All);

    [SkippableTheory]
    [MemberData(nameof(Scenarios))]
    public Task AScenario_AtItsDeclaredThreshold_Holds(string name) =>
        EvalSuite.AssertAsync(name, EvalTier.Full, scenario => scenario.Policy, scorecard);
}

[Trait("Category", "Eval")]
[Trait("Tier", "Full")]
public class MechanismScenarioTests(EvalScorecard scorecard) : IClassFixture<EvalScorecard>
{
    public static TheoryData<string> Scenarios => EvalSuite.Named(MechanismScenarios.All);

    [SkippableTheory]
    [MemberData(nameof(Scenarios))]
    public Task AScenario_AtItsDeclaredThreshold_Holds(string name) =>
        EvalSuite.AssertAsync(name, EvalTier.Full, scenario => scenario.Policy, scorecard);
}

[Trait("Category", "Eval")]
[Trait("Tier", "Full")]
public class VoiceScenarioTests(EvalScorecard scorecard) : IClassFixture<EvalScorecard>
{
    public static TheoryData<string> Scenarios => EvalSuite.Named(VoiceScenarios.All, 0, of: 2);

    [SkippableTheory]
    [MemberData(nameof(Scenarios))]
    public Task AScenario_AtItsDeclaredThreshold_Holds(string name) =>
        EvalSuite.AssertAsync(name, EvalTier.Full, scenario => scenario.Policy, scorecard);
}

[Trait("Category", "Eval")]
[Trait("Tier", "Full")]
public class VoiceScenarioTests2(EvalScorecard scorecard) : IClassFixture<EvalScorecard>
{
    public static TheoryData<string> Scenarios => EvalSuite.Named(VoiceScenarios.All, 1, of: 2);

    [SkippableTheory]
    [MemberData(nameof(Scenarios))]
    public Task AScenario_AtItsDeclaredThreshold_Holds(string name) =>
        EvalSuite.AssertAsync(name, EvalTier.Full, scenario => scenario.Policy, scorecard);
}

[Trait("Category", "Eval")]
[Trait("Tier", "Full")]
public class HomeAssistantScenarioTests(EvalScorecard scorecard) : IClassFixture<EvalScorecard>
{
    public static TheoryData<string> Scenarios => EvalSuite.Named(HomeAssistantScenarios.All, 0, of: 2);

    [SkippableTheory]
    [MemberData(nameof(Scenarios))]
    public Task AScenario_AtItsDeclaredThreshold_Holds(string name) =>
        EvalSuite.AssertAsync(name, EvalTier.Full, scenario => scenario.Policy, scorecard);
}

[Trait("Category", "Eval")]
[Trait("Tier", "Full")]
public class HomeAssistantScenarioTests2(EvalScorecard scorecard) : IClassFixture<EvalScorecard>
{
    public static TheoryData<string> Scenarios => EvalSuite.Named(HomeAssistantScenarios.All, 1, of: 2);

    [SkippableTheory]
    [MemberData(nameof(Scenarios))]
    public Task AScenario_AtItsDeclaredThreshold_Holds(string name) =>
        EvalSuite.AssertAsync(name, EvalTier.Full, scenario => scenario.Policy, scorecard);
}

// Ten scenarios, split in two like the Home Assistant family they belong beside.
[Trait("Category", "Eval")]
[Trait("Tier", "Full")]
public class WatchScenarioTests(EvalScorecard scorecard) : IClassFixture<EvalScorecard>
{
    public static TheoryData<string> Scenarios => EvalSuite.Named(WatchScenarios.All, 0, of: 2);

    [SkippableTheory]
    [MemberData(nameof(Scenarios))]
    public Task AScenario_AtItsDeclaredThreshold_Holds(string name) =>
        EvalSuite.AssertAsync(name, EvalTier.Full, scenario => scenario.Policy, scorecard);
}

[Trait("Category", "Eval")]
[Trait("Tier", "Full")]
public class WatchScenarioTests2(EvalScorecard scorecard) : IClassFixture<EvalScorecard>
{
    public static TheoryData<string> Scenarios => EvalSuite.Named(WatchScenarios.All, 1, of: 2);

    [SkippableTheory]
    [MemberData(nameof(Scenarios))]
    public Task AScenario_AtItsDeclaredThreshold_Holds(string name) =>
        EvalSuite.AssertAsync(name, EvalTier.Full, scenario => scenario.Policy, scorecard);
}

[Trait("Category", "Eval")]
[Trait("Tier", "Full")]
public class VaultScenarioTests(EvalScorecard scorecard) : IClassFixture<EvalScorecard>
{
    public static TheoryData<string> Scenarios => EvalSuite.Named(VaultScenarios.All, 0, of: 2);

    [SkippableTheory]
    [MemberData(nameof(Scenarios))]
    public Task AScenario_AtItsDeclaredThreshold_Holds(string name) =>
        EvalSuite.AssertAsync(name, EvalTier.Full, scenario => scenario.Policy, scorecard);
}

[Trait("Category", "Eval")]
[Trait("Tier", "Full")]
public class VaultScenarioTests2(EvalScorecard scorecard) : IClassFixture<EvalScorecard>
{
    public static TheoryData<string> Scenarios => EvalSuite.Named(VaultScenarios.All, 1, of: 2);

    [SkippableTheory]
    [MemberData(nameof(Scenarios))]
    public Task AScenario_AtItsDeclaredThreshold_Holds(string name) =>
        EvalSuite.AssertAsync(name, EvalTier.Full, scenario => scenario.Policy, scorecard);
}

[Trait("Category", "Eval")]
[Trait("Tier", "Full")]
public class MountScenarioTests(EvalScorecard scorecard) : IClassFixture<EvalScorecard>
{
    public static TheoryData<string> Scenarios => EvalSuite.Named(MountScenarios.All);

    [SkippableTheory]
    [MemberData(nameof(Scenarios))]
    public Task AScenario_AtItsDeclaredThreshold_Holds(string name) =>
        EvalSuite.AssertAsync(name, EvalTier.Full, scenario => scenario.Policy, scorecard);
}

[Trait("Category", "Eval")]
[Trait("Tier", "Full")]
public class DelegationScenarioTests(EvalScorecard scorecard) : IClassFixture<EvalScorecard>
{
    public static TheoryData<string> Scenarios => EvalSuite.Named(DelegationScenarios.All);

    [SkippableTheory]
    [MemberData(nameof(Scenarios))]
    public Task AScenario_AtItsDeclaredThreshold_Holds(string name) =>
        EvalSuite.AssertAsync(name, EvalTier.Full, scenario => scenario.Policy, scorecard);
}

[Trait("Category", "Eval")]
[Trait("Tier", "Full")]
public class MemoryScenarioTests(EvalScorecard scorecard) : IClassFixture<EvalScorecard>
{
    public static TheoryData<string> Scenarios => EvalSuite.Named(MemoryScenarios.All);

    [SkippableTheory]
    [MemberData(nameof(Scenarios))]
    public Task AScenario_AtItsDeclaredThreshold_Holds(string name) =>
        EvalSuite.AssertAsync(name, EvalTier.Full, scenario => scenario.Policy, scorecard);
}

[Trait("Category", "Eval")]
[Trait("Tier", "Full")]
public class MusicScenarioTests(EvalScorecard scorecard) : IClassFixture<EvalScorecard>
{
    public static TheoryData<string> Scenarios => EvalSuite.Named(MusicScenarios.All);

    [SkippableTheory]
    [MemberData(nameof(Scenarios))]
    public Task AScenario_AtItsDeclaredThreshold_Holds(string name) =>
        EvalSuite.AssertAsync(name, EvalTier.Full, scenario => scenario.Policy, scorecard);
}

[Trait("Category", "Eval")]
[Trait("Tier", "Full")]
public class WebScenarioTests(EvalScorecard scorecard) : IClassFixture<EvalScorecard>
{
    public static TheoryData<string> Scenarios => EvalSuite.Named(WebScenarios.All, 0, of: 2);

    [SkippableTheory]
    [MemberData(nameof(Scenarios))]
    public Task AScenario_AtItsDeclaredThreshold_Holds(string name) =>
        EvalSuite.AssertAsync(name, EvalTier.Full, scenario => scenario.Policy, scorecard);
}

[Trait("Category", "Eval")]
[Trait("Tier", "Full")]
public class WebScenarioTests2(EvalScorecard scorecard) : IClassFixture<EvalScorecard>
{
    public static TheoryData<string> Scenarios => EvalSuite.Named(WebScenarios.All, 1, of: 2);

    [SkippableTheory]
    [MemberData(nameof(Scenarios))]
    public Task AScenario_AtItsDeclaredThreshold_Holds(string name) =>
        EvalSuite.AssertAsync(name, EvalTier.Full, scenario => scenario.Policy, scorecard);
}

// The seam the split opened: a family added to EvalSuite.All without a class here would report
// its claims as covered while nothing executes them, and a scenario wired into two classes would
// be recorded twice. Deterministic, so a bare run catches both before any money is spent.
public class ScenarioWiringTests
{
    // What one class may hold. A class is a collection and a collection is serial, so its
    // scenarios are a chain the rest of the pass ends up waiting on — the pass is only as short
    // as its longest class, however wide the run gate is opened.
    private const int ShardCeiling = 6;

    [Fact]
    public void EveryScenario_IsWiredIntoExactlyOneFullTierClass()
    {
        var wired = Classes("Tier", "Full").SelectMany(Named).ToList();

        wired.ShouldBe(EvalSuite.All.Select(s => s.Name).ToList(), ignoreOrder: true);
    }

    [Fact]
    public void NoScenarioClass_HoldsMoreThanTheShardCeiling()
    {
        var longest = Classes("Category", "Eval")
            .Select(type => (Class: type.Name, Count: Named(type).Count()))
            .Where(held => held.Count > ShardCeiling)
            .ToList();

        // A family that outgrows the ceiling takes another shard, not a longer chain.
        longest.ShouldBeEmpty(
            $"these classes are longer than {ShardCeiling} scenarios: "
            + string.Join(", ", longest.Select(held => $"{held.Class} ({held.Count})")));
    }

    private static IEnumerable<Type> Classes(string trait, string value) =>
        typeof(ScenarioWiringTests).Assembly.GetTypes()
            .Where(type => !type.IsAbstract && type.CustomAttributes.Any(attribute =>
                attribute.AttributeType == typeof(TraitAttribute)
                && Equals(attribute.ConstructorArguments[0].Value, trait)
                && Equals(attribute.ConstructorArguments[1].Value, value)));

    private static IEnumerable<string> Named(Type type) =>
        ((IEnumerable<object[]>)type
            .GetProperty("Scenarios", BindingFlags.Public | BindingFlags.Static)!
            .GetValue(null)!)
            .Select(row => (string)row[0]);
}