using Tests.Eval.Harness;

namespace Tests.Eval.Fixtures;

// Whether a pass takes every declared run or stops each scenario at its threshold. Stopping is
// the default (ADR-0040); the switch is what a model-bump diff sets, so both scorecards rate every
// scenario over the same N.
public static class EvalRuns
{
    public const string Variable = "ZIGGURAT_EVAL_EXHAUSTIVE";

    public static RunPolicy Applied(RunPolicy declared) =>
        Applied(declared, Environment.GetEnvironmentVariable(Variable));

    public static RunPolicy Applied(RunPolicy declared, string? asked) =>
        asked?.Trim() is "1" or "true" ? declared with { Exhaustive = true } : declared;
}