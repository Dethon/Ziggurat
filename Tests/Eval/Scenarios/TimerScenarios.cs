using Domain.Prompts;
using Tests.Eval.Harness;

namespace Tests.Eval.Scenarios;

// The timer contract, as scenarios. Every one of these was demonstrated red by deleting the prose
// of the claim it cites — a scenario that stays green without its own claim's prose is measuring
// the model's default behaviour and protects nothing.
public static class TimerScenarios
{
    public static IReadOnlyList<Scenario> All =>
        [PastaTimer, ExtendARunningTimer, WhatTimersExist, CancelTheTimer, TheErrandGoesInTheText];

    // The tracer bullet: one voice turn, one countdown, at the path the contract names. It cites
    // one claim rather than the three it looks like it exercises — the other two were demonstrated
    // and stayed green without their prose, so they are guards declared by the mechanism scenarios
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
        Instant = EvalInstant.Evening,
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
        Permitted = [.. CallPermission.Looking("/timers*")],
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
        Instant = EvalInstant.Evening,
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
        Permitted = [.. CallPermission.Looking("/timers*")],
        Ordering =
        [
            new OrderingConstraint("status", "delete"),
            new OrderingConstraint("delete", "recreate")
        ],
        CallCeiling = 6,
        // The delete and recreate is internal: what the user asked for was two more minutes, and
        // the machinery behind that answer is not something they asked to hear about.
        Reply = new ReplyExpectation
        {
            Spoken = true,
            NeverSays = ["borr", "elimin", "recre", "de nuevo", "otro temporizador"]
        },
        Claims =
        [
            TimerPrompt.ChangedByDeleteAndRecreate.Id,
            TimerPrompt.StatusIsReadForTimeLeft.Id,
            TimerPrompt.RecreationIsNeverNarrated.Id
        ],
        Policy = new RunPolicy(2, 3)
    };

    // Two timers running, and a question about all of them: the listing is a glob of /timers, and
    // the answer is a list — the one turn shape where the voice contract allows more than one
    // sentence, still bounded at three. The remaining times are 5 and 50 minutes, distinct at a
    // word boundary in both spellings, so the reply cannot pass by carrying the wrong one.
    public static Scenario WhatTimersExist => new()
    {
        Name = "the timers that exist are answered from a glob",
        AgentId = "nabu",
        Turn = new EvalTurn
        {
            Text = "¿qué temporizadores tengo puestos y cuánto les queda?",
            Sender = "fran",
            Room = "kitchen",
            SatelliteId = "kitchen-01"
        },
        Instant = EvalInstant.Evening,
        Armed =
        [
            new ArmedTimerSeed("pasta", DurationSeconds: 480, Room: "kitchen",
                RunningFor: TimeSpan.FromMinutes(3)),
            new ArmedTimerSeed("lavadora", DurationSeconds: 3600, Room: "kitchen",
                RunningFor: TimeSpan.FromMinutes(10))
        ],
        Required =
        [
            new CallExpectation
            {
                Label = "list",
                Tool = EvalTools.Glob,
                Arguments = [Arg.PathMatches("^/timers")]
            }
        ],
        Permitted = [.. CallPermission.Looking("/timers*")],
        // The glob, one status per timer, and one spare.
        CallCeiling = 5,
        Reply = new ReplyExpectation
        {
            Spoken = true,
            MaxSentences = 3,
            Mentions =
            [
                new SpokenValue("the pasta timer", "pasta"),
                new SpokenValue("its remaining time", "cinco", "5"),
                new SpokenValue("the laundry timer", "lavadora", "colada"),
                new SpokenValue("its remaining time", "cincuenta", "50")
            ]
        },
        Claims = [TimerPrompt.ListedByGlob.Id, VoicePrompt.SeveralSentencesOnlyWhenAsked.Id],
        Policy = new RunPolicy(2, 3)
    };

    // Cancelling is a remove of the timer's own directory — not a dismiss (nothing is ringing)
    // and not an edit (timers are immutable).
    public static Scenario CancelTheTimer => new()
    {
        Name = "a timer is cancelled by removing it",
        AgentId = "nabu",
        Turn = new EvalTurn
        {
            Text = "quita el temporizador de la pasta",
            Sender = "fran",
            Room = "kitchen",
            SatelliteId = "kitchen-01"
        },
        Instant = EvalInstant.Evening,
        Armed =
        [
            new ArmedTimerSeed("pasta", DurationSeconds: 480, Room: "kitchen",
                RunningFor: TimeSpan.FromMinutes(3))
        ],
        Required =
        [
            new CallExpectation
            {
                Label = "cancel",
                Tool = EvalTools.Remove,
                Arguments = [Arg.Path("/timers/pasta")]
            }
        ],
        Permitted = [.. CallPermission.Looking("/timers*")],
        CallCeiling = 3,
        Reply = new ReplyExpectation { Spoken = true, MaxSentences = 1 },
        Claims = [TimerPrompt.CancelledByRemovingIt.Id],
        Policy = new RunPolicy(2, 3)
    };

    // A reminder whose errand could be misread as a job: "que apague el horno" is something the
    // *person* will do when told, so it belongs in the timer's text — the message spoken when it
    // fires. A model that reads it as its own task writes a /schedules one-shot instead, which
    // nothing here permits; one that creates the timer but leaves the errand out of text fires a
    // countdown that announces "recordatorio timer" and tells nobody what for.
    public static Scenario TheErrandGoesInTheText => new()
    {
        Name = "the reminder's errand lands in the timer's text",
        AgentId = "nabu",
        Turn = new EvalTurn
        {
            Text = "recuérdame en veinte minutos que apague el horno",
            Sender = "fran",
            Room = "kitchen",
            SatelliteId = "kitchen-01"
        },
        Instant = EvalInstant.Evening,
        Required =
        [
            new CallExpectation
            {
                Label = "create",
                Tool = EvalTools.Create,
                Arguments =
                [
                    Arg.PathMatches(@"^/timers/[^/]+/timer\.json$"),
                    Arg.Body("content",
                        Arg.Number("durationSeconds", 1200),
                        Arg.Matches("text", "(?i)horno"))
                ]
            }
        ],
        Permitted = [.. CallPermission.Looking("/timers*")],
        CallCeiling = 4,
        Claims = [TimerPrompt.TextIsSpokenNeverAnInstruction.Id],
        Policy = new RunPolicy(2, 3)
    };
}