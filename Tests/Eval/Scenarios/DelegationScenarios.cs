using Domain.Prompts;
using Tests.Eval.Harness;

namespace Tests.Eval.Scenarios;

// Whether to hand the work to somebody else. What is under test is the parent's decision and the
// prompt it wrote, never the worker's answer — the workers are canned, so a scenario here cannot
// fail on what a second model happened to say.
//
// Only the negative half is here. The positive one — two independent parts, two workers — was
// written, run and withdrawn: see the exemption on `subagents.parallel-parts-are-delegated`, which
// is the most interesting thing this family found.
public static class DelegationScenarios
{
    private static readonly DateTimeOffset _evening =
        new(2026, 8, 17, 20, 0, 0, TimeSpan.FromHours(2));

    public static IReadOnlyList<Scenario> All => [OneLookupIsDoneInPlace];

    // One read answers this, and a worker would make it slower and no better. Declaring no
    // delegation is how the scenario says so: any worker at all fails it.
    public static Scenario OneLookupIsDoneInPlace => new()
    {
        Name = "a one-call lookup is not handed to a worker",
        AgentId = "nabu",
        Turn = new EvalTurn
        {
            Text = "¿cuánto queda del temporizador de la pasta?",
            Sender = "fran",
            Room = "kitchen",
            SatelliteId = "kitchen-01"
        },
        Instant = _evening,
        Armed =
        [
            new ArmedTimerSeed("pasta", DurationSeconds: 480, Room: "kitchen",
                RunningFor: TimeSpan.FromMinutes(3))
        ],
        Required =
        [
            new CallExpectation
            {
                Label = "status",
                Tool = EvalTools.Read,
                Arguments = [Arg.Path("/timers/pasta/status.json")]
            }
        ],
        Permitted =
        [
            new CallPermission(EvalTools.Glob, "/timers*"),
            new CallPermission(EvalTools.Read, "/timers*"),
            new CallPermission(EvalTools.Info, "/timers*")
        ],
        CallCeiling = 4,
        // No citation: with the do-it-yourself bullet deleted the model still read the status
        // itself. Delegating a one-call lookup is not a temptation it has.
        Policy = new RunPolicy(2, 3)
    };
}