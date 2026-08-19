using Domain.Prompts;
using Tests.Eval.Fixtures;
using Tests.Eval.Harness;

namespace Tests.Eval.Scenarios;

// The three-way discrimination the timer contract exists for: at the appointed moment, is a person
// being told something, or does something have to happen? And if a person is being told, is the
// time a duration or a clock time? Each scenario runs against a stack hosting all three mounts, so
// the wrong answer is always available — a discrimination with one option is not one.
public static class MechanismScenarios
{
    public static IReadOnlyList<Scenario> All =>
    [
        RemindMeInTenMinutes, TurnTheAirOffInAnHour, WakeMeAtSeven, SixHoursFromNow,
        FromTheOfficeAboutTheKitchen, NoSatelliteAtAll
    ];

    // A person is being told something, and the time is a duration: a countdown, with the message
    // to speak in `text`. Nothing else in the home is touched — with /schedules and the alarms
    // calendar both mounted, "somewhere else it could have gone" is not hypothetical.
    public static Scenario RemindMeInTenMinutes => new()
    {
        Name = "a ten-minute reminder is a countdown",
        AgentId = "nabu",
        Turn = new EvalTurn
        {
            Text = "recuérdame en diez minutos que saque la basura",
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
                    Arg.Body("content", Arg.Number("durationSeconds", 600))
                ]
            }
        ],
        Permitted = [.. CallPermission.Looking("/timers*")],
        CallCeiling = 4,
        // No deterministic citation: both rules were deleted from every prompt that teaches them
        // and this still passed. It runs as a regression guard rather than as evidence — see
        // ClaimExemptions. The judged check is separate: whether the id is descriptive is a
        // judgement about a word, and this is the scenario whose create call carries one.
        Judged =
        [
            new JudgedCheck(TimerPrompt.IdIsDescriptive.Id,
                "The user asked to be reminded to take out the rubbish ('que saque la basura'). "
                + "Find the tool call that created the timer: its path is /timers/<id>/timer.json. "
                + "Judge the <id> alone. Pass if the id names what the timer is for — the rubbish, "
                + "the errand — so a person hearing '<id> timer' would know what fired. Fail a "
                + "generic id ('timer', 'reminder', 'recordatorio', 'aviso'), a bare number, or a "
                + "random string.")
        ],
        Policy = new RunPolicy(2, 3)
    };

    // The same duration phrasing, and a different mechanism, because it is the agent that has to
    // act when the moment comes. A timer only speaks, so a timer here would leave the air
    // conditioning running and announce that it had not been turned off.
    public static Scenario TurnTheAirOffInAnHour => new()
    {
        Name = "an action in an hour is a scheduled one-shot",
        AgentId = "nabu",
        Turn = new EvalTurn
        {
            Text = "apaga el aire del salón dentro de una hora",
            Sender = "fran",
            Room = "kitchen",
            SatelliteId = "kitchen-01"
        },
        Instant = EvalInstant.Evening,
        Required =
        [
            new CallExpectation
            {
                Label = "schedule",
                Tool = EvalTools.Create,
                Arguments =
                [
                    Arg.PathMatches(@"^/schedules/[^/]+/[^/]+/schedule\.json$"),
                    // 21:00 in Madrid, whichever of the three spellings the contract allows. What
                    // is pinned is the instant, never the model's choice of how to write a zone.
                    Arg.Body("content", Arg.Any(
                        Arg.Matches("runAt", @"^2026-08-17T21:00(:00)?(\.0+)?(\+02:00)?$"),
                        Arg.Matches("runAt", @"^2026-08-17T19:00(:00)?(\.0+)?Z$")))
                ]
            }
        ],
        // Reading the home is how the prompt for the scheduled action gets the entity right, so
        // looking around /ha is tolerated; acting on it now is not, and neither is a timer.
        Permitted =
        [
            new CallPermission(EvalTools.Glob, "/schedules*"),
            new CallPermission(EvalTools.Read, "/schedules*"),
            new CallPermission(EvalTools.Info, "/schedules*"),
            new CallPermission(EvalTools.Glob, "/ha*"),
            new CallPermission(EvalTools.Read, "/ha*"),
            new CallPermission(EvalTools.Info, "/ha*")
        ],
        CallCeiling = 6,
        // No citation, for the same reason: with the two-step choice deleted from all three
        // prompts the model still worked out that a timer cannot switch anything off.
        Policy = new RunPolicy(2, 3)
    };

    // A person is being told something at a clock time: the alarms calendar, which survives a
    // restart and can escalate to the phone. A countdown here would be silently lost to one.
    public static Scenario WakeMeAtSeven => new()
    {
        Name = "a clock time is a calendar alarm",
        AgentId = "nabu",
        Turn = new EvalTurn
        {
            Text = "despiértame mañana a las siete de la mañana",
            Sender = "fran",
            Room = "office",
            SatelliteId = "office-01"
        },
        Instant = EvalInstant.Evening,
        Required =
        [
            new CallExpectation
            {
                Label = "alarm",
                Tool = EvalTools.Exec,
                Arguments =
                [
                    Arg.Path(FakeHomeAssistant.AlarmsDirectory),
                    Arg.Matches("command", @"^create_event\.sh\b"),
                    Arg.Matches("command", @"2026-08-18[ T]07:00")
                ]
            }
        ],
        // Reading an action's arguments before writing one is what the mount tells the agent to
        // do, and `--help` is an exec like any other — so exec is tolerated on the alarms
        // directory, and nowhere else in the home.
        Permitted =
        [
            .. CallPermission.Looking("/ha*"),
            new CallPermission(EvalTools.Exec, FakeHomeAssistant.AlarmsDirectory)
        ],
        CallCeiling = 6,
        // No citation: the calendar entity is called Assistant Alarms, which teaches the rule the
        // prose teaches and cannot be deleted without deleting the calendar.
        Policy = new RunPolicy(2, 3),
        // This family's canary: the three-way choice on the one turn where the wrong answer is a
        // countdown that a restart would silently lose.
        Tier = EvalTier.Smoke
    };

    // Past the four-hour ceiling, phrased as a duration — the one case where the phrasing points
    // at a countdown and the contract sends it to the calendar anyway.
    public static Scenario SixHoursFromNow => new()
    {
        Name = "six hours from now is past the timer ceiling",
        AgentId = "nabu",
        Turn = new EvalTurn
        {
            Text = "avísame dentro de seis horas que tengo que llamar a mi madre",
            Sender = "fran",
            Room = "office",
            SatelliteId = "office-01"
        },
        Instant = EvalInstant.Evening,
        Required =
        [
            new CallExpectation
            {
                Label = "alarm",
                Tool = EvalTools.Exec,
                Arguments =
                [
                    Arg.Path(FakeHomeAssistant.AlarmsDirectory),
                    Arg.Matches("command", @"^create_event\.sh\b"),
                    Arg.Matches("command", @"2026-08-18[ T]02:00")
                ]
            }
        ],
        Permitted =
        [
            .. CallPermission.Looking("/ha*"),
            new CallPermission(EvalTools.Exec, FakeHomeAssistant.AlarmsDirectory)
        ],
        CallCeiling = 6,
        Claims = [TimerPrompt.DurationCappedAtFourHours.Id],
        Policy = new RunPolicy(2, 3)
    };

    // The speaking room against a room the words suggest: the request is about the pasta and it
    // arrives from the office, so a model reading the topic rather than the turn rings in the
    // kitchen — where nobody is standing.
    public static Scenario FromTheOfficeAboutTheKitchen => new()
    {
        Name = "a timer rings where the request came from",
        AgentId = "nabu",
        Turn = new EvalTurn
        {
            Text = "pon un temporizador de ocho minutos para la pasta",
            Sender = "fran",
            Room = "office",
            SatelliteId = "office-01"
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
                    Arg.Body("content", Arg.Body("target", Arg.Any(
                        Arg.Is("room", "office"), Arg.Is("satelliteId", "office-01"))))
                ]
            }
        ],
        Permitted = [.. CallPermission.Looking("/timers*")],
        CallCeiling = 4,
        // No citation: the office won even with the rule deleted from the prompt and the mount.
        // A turn whose decoration names a room is answered with that room by default.
        Policy = new RunPolicy(2, 3)
    };

    // The same request with no satellite on it: nothing was spoken, so there is no speaking room to
    // default to, and a guess rings in an empty room or fails to arm at all. What must happen is
    // that the agent asks — and, until the reply checks land, what is asserted is that it created
    // nothing while asking.
    //
    // Withdrawn on 2026-08-19 — with the memory section in the prompt the model guessed a kitchen
    // timer instead of asking, two runs out of three — and restored when the rule was rewritten to
    // say that nothing but the turn supplies a room: not the topic, not a memory, not a default.
    // See .scratch/findings-from-the-eval/issues/01-timer-does-not-ask-which-room.md.
    public static Scenario NoSatelliteAtAll => new()
    {
        Name = "a timer with no speaking room is asked about",
        AgentId = "jonas",
        Turn = new EvalTurn
        {
            Text = "pon un temporizador de ocho minutos para la pasta",
            Sender = "fran"
        },
        Instant = EvalInstant.Evening,
        Permitted = [.. CallPermission.Looking("/timers*")],
        CallCeiling = 3,
        Reply = new ReplyExpectation
        {
            // Creating nothing is half of it; a silent refusal would pass that half. What the
            // contract asks for is a question, and the roster gives it two rooms to offer.
            Mentions =
            [
                new SpokenValue("where it should ring",
                    "habitación", "sala", "dónde", "cocina", "oficina", "despacho", "satélite")
            ]
        },
        Claims = [TimerPrompt.NoSatelliteAsksWhichRoom.Id],
        Policy = new RunPolicy(2, 3)
    };
}