using Domain.Prompts;
using Tests.Eval.Harness;

namespace Tests.Eval.Scenarios;

// The timer contract, as scenarios. Every one of these was demonstrated red by deleting the prose
// of the claim it cites — a scenario that stays green without its own claim's prose is measuring
// the model's default behaviour and protects nothing.
public static class TimerScenarios
{
    // A Monday evening in Madrid. Fixed rather than relative, because the whole point of pinning
    // an instant is that an expected fire time is a string somebody can read in a dump.
    private static readonly DateTimeOffset _evening =
        new(2026, 8, 17, 20, 0, 0, TimeSpan.FromHours(2));

    public static IReadOnlyList<Scenario> All => [PastaTimer, ExtendARunningTimer];

    // The tracer bullet: one voice turn, one countdown, at the path the contract names. It cites
    // one claim rather than the three it looks like it exercises — the other two were demonstrated
    // and stayed green without their prose, so they are exemptions with that finding written down
    // rather than citations this scenario cannot support.
    public static Scenario PastaTimer => new()
    {
        Name = "an eight-minute pasta timer",
        AgentId = "nabu",
        Turn = new EvalTurn
        {
            Text = "pon un temporizador de ocho minutos para la pasta",
            Sender = "fran",
            Room = "kitchen",
            SatelliteId = "kitchen-01"
        },
        Instant = _evening,
        Required =
        [
            new CallExpectation
            {
                Label = "create",
                Tool = EvalTools.Create,
                Arguments =
                [
                    // The id is the model's to choose, so what is pinned is the shape the contract
                    // states: one directory per timer, holding timer.json.
                    Arg.PathMatches(@"^/timers/[^/]+/timer\.json$"),
                    Arg.Body("content",
                        Arg.Number("durationSeconds", 480),
                        // Targeted without being told, from the room the request came from —
                        // named as the room or as the id of the satellite in it, which the contract
                        // offers as two spellings of the same answer.
                        Arg.Body("target", Arg.Any(
                            Arg.Is("room", "kitchen"), Arg.Is("satelliteId", "kitchen-01"))))
                ]
            }
        ],
        // A model that looks before it writes is not doing anything unnecessary; one that writes
        // to /schedules or the calendar is, and neither is permitted here.
        Permitted =
        [
            new CallPermission(EvalTools.Glob, "/timers*"),
            new CallPermission(EvalTools.Read, "/timers*"),
            new CallPermission(EvalTools.Info, "/timers*")
        ],
        CallCeiling = 4,
        Claims = [TimerPrompt.CreatedAtItsOwnPath.Id],
        Policy = new RunPolicy(2, 3),
        Tier = EvalTier.Smoke
    };

    // The ordering case: a running timer is immutable, so changing one is read, delete, create —
    // in that order, with anything else in between nobody's business.
    public static Scenario ExtendARunningTimer => new()
    {
        Name = "extend a running timer by two minutes",
        AgentId = "nabu",
        Turn = new EvalTurn
        {
            Text = "añade dos minutos al temporizador de la pasta",
            Sender = "fran",
            Room = "kitchen",
            SatelliteId = "kitchen-01"
        },
        Instant = _evening,
        // Eight minutes armed, three of them spent: the remaining five is a number that exists
        // only in status.json, so a model that read the timer's spec instead writes 600 and the
        // scenario says so.
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
            },
            new CallExpectation
            {
                Label = "delete",
                Tool = EvalTools.Remove,
                Arguments = [Arg.Path("/timers/pasta")]
            },
            new CallExpectation
            {
                Label = "recreate",
                Tool = EvalTools.Create,
                Arguments =
                [
                    Arg.PathMatches(@"^/timers/[^/]+/timer\.json$"),
                    // Five minutes were left and two were asked for: the remainder is read rather
                    // than guessed, so anything but 420 means the status read did not inform the
                    // write.
                    Arg.Body("content", Arg.Number("durationSeconds", 420))
                ]
            }
        ],
        // Reading anything under /timers is tolerated: what the contract discriminates is which
        // file the new duration came from, and the required status read plus the remainder pin
        // that far better than forbidding a look at the spec would.
        Permitted =
        [
            new CallPermission(EvalTools.Glob, "/timers*"),
            new CallPermission(EvalTools.Info, "/timers*"),
            new CallPermission(EvalTools.Read, "/timers*")
        ],
        Ordering =
        [
            new OrderingConstraint("status", "delete"),
            new OrderingConstraint("delete", "recreate")
        ],
        CallCeiling = 6,
        Claims =
        [
            TimerPrompt.ChangedByDeleteAndRecreate.Id,
            TimerPrompt.StatusIsReadForTimeLeft.Id
        ],
        Policy = new RunPolicy(2, 3)
    };
}