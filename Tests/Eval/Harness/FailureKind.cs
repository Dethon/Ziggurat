namespace Tests.Eval.Harness;

// Why a run went red, at the one fork a skill introduces: the model never loaded the skill the
// scenario requires, or every required load happened and a checked behaviour still did not hold.
// The first says to edit the description, the second the body — and a scenario requiring no load
// can only fail the second way (docs/adr/0039).
public enum FailureKind
{
    SkillNotLoaded,
    RuleIgnored
}

public static class FailureKinds
{
    public static string Spelled(this FailureKind kind) => kind switch
    {
        FailureKind.SkillNotLoaded => "skill not loaded",
        _ => "rule ignored"
    };

    // The scorecard's key for each kind, so a diff between two passes reads the same word.
    public static string Key(this FailureKind kind) => kind switch
    {
        FailureKind.SkillNotLoaded => "skillNotLoaded",
        _ => "ruleIgnored"
    };
}