using Tests.Eval.Harness;

namespace Tests.Eval;

// The changed tier: the scenarios whose claims a diff touched, at their declared thresholds.
// What a prompt author runs between edits, where a full pass would pay for seventy scenarios to
// learn about the three the edit could have broken. The full tier stays the gate before a merge:
// this one answers only "did this prompt edit break what it teaches" (ADR-0041).
//
// Four shards, because a class is a serial chain; which scenarios land in each is the diff's.
[Trait("Category", "Eval")]
[Trait("Tier", "Changed")]
public class ChangedScenarioTests(EvalScorecard scorecard) : IClassFixture<EvalScorecard>
{
    public static TheoryData<string> Scenarios => EvalSuite.Named(EvalTier.Changed, 0, of: 4);

    [SkippableTheory]
    [MemberData(nameof(Scenarios))]
    public Task AScenarioTheDiffTouched_AtItsDeclaredThreshold_Holds(string name) =>
        EvalSuite.AssertAsync(name, EvalTier.Changed, scenario => scenario.Policy, scorecard);
}

[Trait("Category", "Eval")]
[Trait("Tier", "Changed")]
public class ChangedScenarioTests2(EvalScorecard scorecard) : IClassFixture<EvalScorecard>
{
    public static TheoryData<string> Scenarios => EvalSuite.Named(EvalTier.Changed, 1, of: 4);

    [SkippableTheory]
    [MemberData(nameof(Scenarios))]
    public Task AScenarioTheDiffTouched_AtItsDeclaredThreshold_Holds(string name) =>
        EvalSuite.AssertAsync(name, EvalTier.Changed, scenario => scenario.Policy, scorecard);
}

[Trait("Category", "Eval")]
[Trait("Tier", "Changed")]
public class ChangedScenarioTests3(EvalScorecard scorecard) : IClassFixture<EvalScorecard>
{
    public static TheoryData<string> Scenarios => EvalSuite.Named(EvalTier.Changed, 2, of: 4);

    [SkippableTheory]
    [MemberData(nameof(Scenarios))]
    public Task AScenarioTheDiffTouched_AtItsDeclaredThreshold_Holds(string name) =>
        EvalSuite.AssertAsync(name, EvalTier.Changed, scenario => scenario.Policy, scorecard);
}

[Trait("Category", "Eval")]
[Trait("Tier", "Changed")]
public class ChangedScenarioTests4(EvalScorecard scorecard) : IClassFixture<EvalScorecard>
{
    public static TheoryData<string> Scenarios => EvalSuite.Named(EvalTier.Changed, 3, of: 4);

    [SkippableTheory]
    [MemberData(nameof(Scenarios))]
    public Task AScenarioTheDiffTouched_AtItsDeclaredThreshold_Holds(string name) =>
        EvalSuite.AssertAsync(name, EvalTier.Changed, scenario => scenario.Policy, scorecard);
}