namespace Tests.Eval.Harness;

// Why a run went red, at the one fork a skill introduces: the model never loaded the skill the
// scenario requires, or every required load happened and a checked behaviour still did not hold.
// The first says to edit the description, the second the body — and a scenario requiring no load
// can only fail the second way (docs/adr/0039).
//
// RunFailed is neither: the provider refused the turn or the deadline passed, so the model never
// answered and nothing it did is evidence about any prose. Kept apart because an outage counted
// as a missing load points the next edit at a description nobody read, on whichever scenario the
// outage happened to land on.
// HostPreloaded is the third thing that is neither: the host put a skill into the conversation
// that the scenario does not permit. The model read whatever it was given and its behaviour is
// not evidence about that skill's prose either way, so counting it as a rule ignored sent the
// next edit to a body nobody had a complaint about. It names the preload instead — the judge's
// question and its bars are what moves it.
public enum FailureKind
{
    RunFailed,
    SkillNotLoaded,
    RuleIgnored,
    HostPreloaded
}

public static class FailureKinds
{
    public static string Spelled(this FailureKind kind) => kind switch
    {
        FailureKind.RunFailed => "run failed",
        FailureKind.SkillNotLoaded => "skill not loaded",
        FailureKind.HostPreloaded => "host preloaded",
        _ => "rule ignored"
    };

    // The two counts that predate the per-run kind list, as that list. Only what a caller that
    // still reports counts can say, which is why a kind added later has to be carried as a kind.
    public static IReadOnlyList<FailureKind> From(int skillNotLoaded, int ruleIgnored) =>
    [
        .. Enumerable.Repeat(FailureKind.SkillNotLoaded, skillNotLoaded),
        .. Enumerable.Repeat(FailureKind.RuleIgnored, ruleIgnored)
    ];

    // The scorecard's key for each kind, so a diff between two passes reads the same word.
    public static string Key(this FailureKind kind) => kind switch
    {
        FailureKind.RunFailed => "runFailed",
        FailureKind.SkillNotLoaded => "skillNotLoaded",
        FailureKind.HostPreloaded => "hostPreloaded",
        _ => "ruleIgnored"
    };
}

// Who loaded the skill a scenario requires. Counted onto the scorecard beside the rate, because
// a trigger claim is now met by either reader of the description and the split is what says
// whether the description still works on the model when Jev abstains.
public enum Loader
{
    None,
    Host,
    Model,
    Nobody
}