using Domain.DTOs.Voice;
using Domain.Prompts;
using Tests.Eval.Fixtures;
using Tests.Eval.Harness;

namespace Tests.Eval.Scenarios;

// Being warned about a person's glucose: a watch is a file under /watchers that Sugary keeps, because
// its triggers are glucose kinds, beside the home's watches. What these assert is the watch Sugary
// stored — whose sensor, what each trigger waits for — read back from its store rather than from the
// file the model wrote, and the home left as it was: a glucose watch written on the home's glucose
// entity is an automation the home did not expect, which shows as an undeclared change.
//
// Jonas's, because he dials Sugary and the home alike: the home's index lists Laura's glucose entity
// too, so every one of these is also the choice between the two kinds of watch. Her log (EvalSugaryLog)
// has a fortnight of readings, her newest 130 and climbing, so a level of 126 (7 mmol/L) is already past.
public static class GlucoseWatchScenarios
{
    public static IReadOnlyList<Scenario> All =>
    [
        ALowWarning, AnAlarmInTheBedroomAtNight, FallingFast, TheSensorStops, LaurasLow, ALevelInMmol
    ];

    private const string WatchFile = @"^/watchers/[^/]+/watch\.json$";

    private static CallExpectation LoadsTheSkill => CallExpectation.LoadsSkill(GlucoseWatchesSkill.Name);

    // Reading her newest reading before the write (the crossing rule), the satellites a room is read
    // from, and the watches already at /watchers, and the home's index and skill, which a run passing
    // through the home's guide reaches for: none of it changes anything, and a write that went to the
    // home instead shows in its snapshot.
    private static IReadOnlyList<CallPermission> Looking =>
    [
        .. CallPermission.Looking("/sugary*"), .. CallPermission.Looking("/watchers*"),
        .. CallPermission.Looking("/timers*"), .. CallPermission.Looking("/ha*"),
        CallPermission.Load(SugarySkill.Name), CallPermission.Load(HomeAssistantSkill.Name)
    ];

    private static string Kind(string kind) => $@"""kind""\s*:\s*""{kind}""";

    private static string Trigger(string kind) => $@"""trigger""\s*:\s*""{kind}""";

    // The household with a bedroom, for the alarm that wakes somebody in it.
    private static readonly SatelliteDescriptor[] WithABedroom =
        [.. EvalStack.Roster, new SatelliteDescriptor("bedroom-01", "bedroom")];

    // Every spelling the target contract allows for one satellite, as the home's watch scenarios read it.
    private static string AnnounceTarget(string room, string satelliteId) =>
        $@"""target""\s*:\s*\{{[^}}]*""(room""\s*:\s*""{room}""|satelliteId""\s*:\s*""{satelliteId}""|satelliteIds""\s*:\s*\[\s*""{satelliteId}""\s*\])";

    // One glucose watch stored, on this sensor, waiting for these triggers, and nothing in the home moved.
    private static IReadOnlyList<StateChange> OneGlucoseWatch(string subject, string triggers) =>
    [
        new StateChange(EvalSugaryLog.WatchCountKey, "1"),
        new StateChange(EvalSugaryLog.SubjectKey(EvalSugaryLog.AnyWatch), subject),
        new StateChange(EvalSugaryLog.TriggersKey(EvalSugaryLog.AnyWatch), triggers)
    ];

    // The plain case: a level, a prompt, no alarm, her own sensor — and none of the home's entity ids.
    public static Scenario ALowWarning => new()
    {
        Name = "a plain low warning is a glucose watch with a prompt",
        AgentId = "jonas",
        Turn = new EvalTurn { Text = "Avísame si me baja el azúcar de 70.", Sender = "fran" },
        Instant = EvalInstant.Evening,
        Required =
        [
            LoadsTheSkill,
            new CallExpectation
            {
                Label = "watch",
                Tool = EvalTools.Create,
                Arguments =
                [
                    Arg.PathMatches(WatchFile),
                    Arg.Matches("content", Trigger("glucose")),
                    Arg.Matches("content", @"""below""\s*:\s*70(?![\d.])"),
                    Arg.Matches("content", Kind("prompt")),
                    Arg.Lacks("content", Kind("announce")),
                    Arg.Lacks("content", "entity_id")
                ]
            }
        ],
        Permitted = Looking,
        Changes = OneGlucoseWatch(EvalRun.UserId, "glucose below 70"),
        Ordering = [new OrderingConstraint("skill", "watch")],
        CallCeiling = 8,
        Claims = [GlucoseWatchesSkill.LoadsForAGlucoseWatchRequest.Id, GlucoseWatchesSkill.PlainWarningIsAPrompt.Id],
        Policy = new RunPolicy(2, 3)
    };

    // Urgency, at night, in a room the request names: an insistent alarm on the bedroom's satellite,
    // kept to the night by a time condition, asked from the chat, where no room is the speaking one.
    public static Scenario AnAlarmInTheBedroomAtNight => new()
    {
        Name = "a night-time glucose alarm rings insistently in the bedroom",
        AgentId = "jonas",
        Turn = new EvalTurn
        {
            Text = "Si por la noche me baja el azúcar de 60, despiértame en el dormitorio con una alarma que suene hasta que la apague.",
            Sender = "fran"
        },
        Instant = EvalInstant.Evening,
        Satellites = WithABedroom,
        Required =
        [
            LoadsTheSkill,
            new CallExpectation
            {
                Label = "watch",
                Tool = EvalTools.Create,
                Arguments =
                [
                    Arg.PathMatches(WatchFile),
                    Arg.Matches("content", Trigger("glucose")),
                    Arg.Matches("content", @"""below""\s*:\s*60(?![\d.])"),
                    Arg.Matches("content", Kind("announce")),
                    Arg.Matches("content", @"""insistent"""),
                    Arg.Matches("content", AnnounceTarget("bedroom", "bedroom-01")),
                    Arg.Matches("content", @"""condition""\s*:\s*""time""")
                ]
            }
        ],
        // Its rooms are read from /timers/satellites.json, which the timers stub says to load the
        // countdown-timers skill before reading: that load is the way to the room, not a wrong skill.
        Permitted = [.. Looking, CallPermission.Load(CountdownTimersSkill.Name)],
        Changes = OneGlucoseWatch(EvalRun.UserId, "glucose below 60"),
        Ordering = [new OrderingConstraint("skill", "watch")],
        CallCeiling = 8,
        Claims =
        [
            GlucoseWatchesSkill.LoadsForAGlucoseWatchRequest.Id,
            GlucoseWatchesSkill.UrgencyIsAnInsistentAlarmInTheRoom.Id
        ],
        Policy = new RunPolicy(2, 3)
    };

    // Falling fast is a rate, not a level: a trend trigger, at the double arrow's rate or near it.
    public static Scenario FallingFast => new()
    {
        Name = "a glucose falling fast is a trend trigger",
        AgentId = "jonas",
        Turn = new EvalTurn { Text = "Avísame si el azúcar me empieza a bajar muy rápido.", Sender = "fran" },
        Instant = EvalInstant.Evening,
        Required =
        [
            LoadsTheSkill,
            new CallExpectation
            {
                Label = "watch",
                Tool = EvalTools.Create,
                Arguments =
                [
                    Arg.PathMatches(WatchFile),
                    Arg.Matches("content", Trigger("glucose_trend")),
                    Arg.Matches("content", @"""falling""\s*:\s*(1\.5|2(\.0+)?|2\.5|3(\.0+)?)(?![\d.])"),
                    Arg.Matches("content", Kind("prompt"))
                ]
            }
        ],
        Permitted = Looking,
        Changes = OneGlucoseWatch(EvalRun.UserId, "glucose_trend falling"),
        Ordering = [new OrderingConstraint("skill", "watch")],
        CallCeiling = 8,
        Claims = [GlucoseWatchesSkill.LoadsForAGlucoseWatchRequest.Id, GlucoseWatchesSkill.FallingFastIsATrendTrigger.Id],
        Policy = new RunPolicy(2, 3)
    };

    // A silent sensor, which is not the link failing: Sugary told apart a sensor that stopped from a
    // provider that did not answer, and the request is about the sensor.
    public static Scenario TheSensorStops => new()
    {
        Name = "a sensor that stops sending is a no_readings watch",
        AgentId = "jonas",
        Turn = new EvalTurn { Text = "Avísame si el sensor deja de mandar lecturas durante media hora.", Sender = "fran" },
        Instant = EvalInstant.Evening,
        Required =
        [
            LoadsTheSkill,
            new CallExpectation
            {
                Label = "watch",
                Tool = EvalTools.Create,
                Arguments =
                [
                    Arg.PathMatches(WatchFile),
                    Arg.Matches("content", Trigger("sensor")),
                    Arg.Matches("content", @"""state""\s*:\s*""no_readings"""),
                    Arg.Matches("content", @"(""minutes""\s*:\s*30|""hours""\s*:\s*0\.5)(?![\d.])")
                ]
            }
        ],
        Permitted = Looking,
        Changes = OneGlucoseWatch(EvalRun.UserId, "sensor no_readings"),
        Ordering = [new OrderingConstraint("skill", "watch")],
        CallCeiling = 8,
        Claims = [GlucoseWatchesSkill.LoadsForAGlucoseWatchRequest.Id, GlucoseWatchesSkill.SilentSensorIsNoReadings.Id],
        Policy = new RunPolicy(2, 3)
    };

    // Somebody else's glucose, for a person the household let read everyone's log: Laura's own sensor,
    // named by her folder under /sugary/people — never the home's glucose entity the index lists under
    // her name, which is what a watch "on Laura's sugar" used to be.
    public static Scenario LaurasLow => new()
    {
        Name = "a watch on another person's glucose names their folder",
        AgentId = "jonas",
        Turn = new EvalTurn { Text = "Avísame si a Laura le baja el azúcar de 70.", Sender = "fran" },
        Instant = EvalInstant.Evening,
        SugaryPeople = EvalSugaryLog.EveryoneReadable,
        Required =
        [
            LoadsTheSkill,
            new CallExpectation
            {
                Label = "watch",
                Tool = EvalTools.Create,
                Arguments =
                [
                    Arg.PathMatches(WatchFile),
                    Arg.Matches("content", Trigger("glucose")),
                    Arg.Matches("content", @"""below""\s*:\s*70(?![\d.])"),
                    Arg.Matches("content", $@"""person""\s*:\s*""{EvalSugaryLog.LauraFolder}"""),
                    Arg.Lacks("content", "entity_id")
                ]
            }
        ],
        Permitted = Looking,
        Changes = OneGlucoseWatch(EvalSugaryLog.LauraUserId, "glucose below 70"),
        Ordering = [new OrderingConstraint("skill", "watch")],
        CallCeiling = 8,
        Claims =
        [
            GlucoseWatchesSkill.LoadsForAGlucoseWatchRequest.Id,
            GlucoseWatchesSkill.AnotherPersonsGlucoseNamesTheirFolder.Id
        ],
        Policy = new RunPolicy(2, 3)
    };

    // A level in mmol/L, written in mg/dL — and already past: her newest reading is 130, so the reply
    // says so and that the watch waits for the next crossing.
    public static Scenario ALevelInMmol => new()
    {
        Name = "a level asked in mmol/L is written in mg/dL and an already-past one is said",
        AgentId = "jonas",
        Turn = new EvalTurn { Text = "Avísame cuando mi glucosa pase de 7 mmol/L.", Sender = "fran" },
        Instant = EvalInstant.Evening,
        Required =
        [
            LoadsTheSkill,
            new CallExpectation
            {
                Label = "watch",
                Tool = EvalTools.Create,
                Arguments =
                [
                    Arg.PathMatches(WatchFile),
                    Arg.Matches("content", Trigger("glucose")),
                    Arg.Matches("content", @"""above""\s*:\s*126(\.\d+)?(?![\d.])"),
                    Arg.Lacks("content", @"""above""\s*:\s*7(?![\d])")
                ]
            }
        ],
        Permitted = Looking,
        Changes = OneGlucoseWatch(EvalRun.UserId, "glucose above 126"),
        Ordering = [new OrderingConstraint("skill", "watch")],
        CallCeiling = 8,
        Claims = [GlucoseWatchesSkill.LoadsForAGlucoseWatchRequest.Id, GlucoseWatchesSkill.LevelsAreMgDl.Id],
        Judged =
        [
            new JudgedCheck(GlucoseWatchesSkill.CrossingOnlyIsSaid.Id,
                "The user asked to be warned when their glucose goes above 7 mmol/L (126 mg/dL), and the "
                + "assistant created a watch for it. Their newest sensor reading was 130 mg/dL (about 7.2 "
                + "mmol/L) and rising, so they were already above the level when the watch was written, and "
                + "such a watch fires only at the next crossing, not now. Pass only if the reply says they "
                + "are already above it, giving the current value in either unit (130 mg/dL, or 7.2 mmol/L "
                + "give or take a tenth), and that the warning will come the next time it crosses (after it "
                + "has come back down). Fail if it does not mention the current value, or if it implies a "
                + "warning is coming now or that they are below the level.")
        ],
        Policy = new RunPolicy(2, 3)
    };
}