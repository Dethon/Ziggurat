using Domain.Prompts;
using Tests.Eval.Fixtures;
using Tests.Eval.Harness;

namespace Tests.Eval.Scenarios;

// Changing one thing in the house, and nothing else. What makes these scenarios different from the
// rest of the suite is that the recording is not the whole story: a permitted call can be the
// right call and still leave three other devices moved, so what they assert on is the home before
// and after the turn.
public static class HomeAssistantScenarios
{
    public static IReadOnlyList<Scenario> All =>
    [
        TurnTheAirConditionerOn, SetTheTemperature, VacuumTheStudy,
        TheWashingMachineByItsListedName, TurboIsTheListedSpelling,
        SnoozeTheMedicationAlarm, TheGarageLightInWriting
    ];

    // "Turn on the AC" and stop: the prompt's own example of the thing not to do is picking a mode
    // or a temperature while you are there. Both of those show up in the diff, which is why the
    // scenario tolerates any exec on that entity rather than trying to forbid one by name.
    public static Scenario TurnTheAirConditionerOn => new()
    {
        Name = "turning the air conditioning on moves nothing else",
        AgentId = "nabu",
        Turn = new EvalTurn
        {
            Text = "enciende el aire del salón",
            Sender = "fran",
            Room = "kitchen",
            SatelliteId = "kitchen-01"
        },
        Instant = EvalInstant.Evening,
        Required =
        [
            new CallExpectation
            {
                Label = "turn on",
                Tool = EvalTools.Exec,
                Arguments =
                [
                    Arg.Path(FakeHomeAssistant.AirConditionerDirectory),
                    Arg.Matches("command", @"^turn_on\.sh")
                ]
            }
        ],
        Permitted =
        [
            new CallPermission(EvalTools.Glob, "/ha*"),
            new CallPermission(EvalTools.Read, "/ha*"),
            new CallPermission(EvalTools.Info, "/ha*"),
            new CallPermission(EvalTools.Search, "/ha*"),
            new CallPermission(EvalTools.Exec, FakeHomeAssistant.AirConditionerDirectory)
        ],
        Changes = [new StateChange(FakeHomeAssistant.AirConditionerEntityId, "on")],
        CallCeiling = 5,
        // No citation: the Scope paragraph was deleted and this still passed. What earns its place
        // is the diff, which is the only thing in the suite that can see a cascade.
        Policy = new RunPolicy(2, 3),
        Tier = EvalTier.Smoke
    };

    // A temperature is set and nothing is read back. Home Assistant stores the new value after a
    // delay, so a read taken now returns the old one and a model that checks its own work concludes
    // it failed. The fake does not reproduce that delay — it applies the call at once — so what
    // enforces the rule here is that reading is not permitted, which is also the only way a
    // scenario can say "and then it stopped".
    public static Scenario SetTheTemperature => new()
    {
        Name = "a temperature is set and never read back",
        AgentId = "nabu",
        Turn = new EvalTurn
        {
            Text = "pon el aire del salón a veintidós grados",
            Sender = "fran",
            Room = "kitchen",
            SatelliteId = "kitchen-01"
        },
        Instant = EvalInstant.Evening,
        Required =
        [
            new CallExpectation
            {
                Label = "set",
                Tool = EvalTools.Exec,
                Arguments =
                [
                    Arg.Path(FakeHomeAssistant.AirConditionerDirectory),
                    Arg.Matches("command", @"^set_temperature\.sh.*\b22\b")
                ]
            }
        ],
        Permitted =
        [
            new CallPermission(EvalTools.Glob, "/ha*"),
            new CallPermission(EvalTools.Info, "/ha*"),
            new CallPermission(EvalTools.Exec, FakeHomeAssistant.AirConditionerDirectory)
        ],
        Changes =
        [
            new StateChange($"{FakeHomeAssistant.AirConditionerEntityId}#temperature", "22")
        ],
        CallCeiling = 5,
        // No citation, for the same reason: with the never-re-read rule gone the model still did
        // not check its own work.
        Policy = new RunPolicy(2, 3)
    };

    // The area was created as "Despacho" and later renamed to "Estudio": HA froze the slug at
    // creation, the display name moved on, and a model deriving the id from what the user said
    // sends an area the registry does not have. The setup index and the /ha/areas tree are the
    // only bridge between the two words.
    public static Scenario VacuumTheStudy => new()
    {
        Name = "an area argument is the slug the files say",
        AgentId = "nabu",
        Turn = new EvalTurn
        {
            Text = "pasa la aspiradora por el estudio",
            Sender = "fran",
            Room = "kitchen",
            SatelliteId = "kitchen-01"
        },
        Instant = EvalInstant.Evening,
        Required =
        [
            new CallExpectation
            {
                Label = "clean",
                Tool = EvalTools.Exec,
                Arguments =
                [
                    Arg.PathMatches(FakeHomeAssistant.VacuumPathPattern),
                    Arg.Matches("command", @"^clean_zone\.sh\b"),
                    Arg.Matches("command",
                        $@"--cleaning_area_id[= ]+""?{FakeHomeAssistant.StudyAreaSlug}\b")
                ]
            }
        ],
        Permitted =
        [
            .. CallPermission.LookingAndManuals("/ha*"),
            new CallPermission(EvalTools.Exec, "*aspiradora*")
        ],
        Changes = [new StateChange(FakeHomeAssistant.VacuumEntityId, "cleaning")],
        CallCeiling = 6,
        Reply = new ReplyExpectation { Spoken = true, MaxSentences = 1 },
        Claims = [HomeAssistantPrompt.AreaSlugIsReadNotDerived.Id],
        Policy = new RunPolicy(2, 3)
    };

    // The directory name is `lavadora_(lavadora)`, composed from the id and the friendly name.
    // Only exec against that exact composed name is permitted, so a bare id or a guessed suffix
    // fails as an unnecessary call — which is the exact-name rule stated as a scenario.
    public static Scenario TheWashingMachineByItsListedName => new()
    {
        Name = "an entity is used by the exact name the listing returned",
        AgentId = "nabu",
        Turn = new EvalTurn
        {
            Text = "pon la lavadora",
            Sender = "fran",
            Room = "kitchen",
            SatelliteId = "kitchen-01"
        },
        Instant = EvalInstant.Evening,
        Required =
        [
            new CallExpectation
            {
                Label = "turn on",
                Tool = EvalTools.Exec,
                Arguments =
                [
                    Arg.Path(FakeHomeAssistant.WashingMachineDirectory),
                    Arg.Matches("command", @"^turn_on\.sh")
                ]
            }
        ],
        Permitted =
        [
            .. CallPermission.Looking("/ha*"),
            new CallPermission(EvalTools.Exec, FakeHomeAssistant.WashingMachineDirectory)
        ],
        Changes = [new StateChange(FakeHomeAssistant.WashingMachineEntityId, "on")],
        CallCeiling = 5,
        Reply = new ReplyExpectation { Spoken = true, MaxSentences = 1 },
        Claims = [HomeAssistantPrompt.EntityNamedAsListed.Id],
        Policy = new RunPolicy(2, 3)
    };

    // "Modo turbo" is the user's word; the service takes one of three options whose casing only
    // the help or the rejection that lists them can supply. The state diff demands the accepted
    // spelling arrived, and the ceiling is where repeating the same wrong shape shows.
    public static Scenario TurboIsTheListedSpelling => new()
    {
        Name = "an action argument is read rather than derived",
        AgentId = "nabu",
        Turn = new EvalTurn
        {
            Text = "pon la aspiradora en modo turbo",
            Sender = "fran",
            Room = "kitchen",
            SatelliteId = "kitchen-01"
        },
        Instant = EvalInstant.Evening,
        Required =
        [
            new CallExpectation
            {
                Label = "set speed",
                Tool = EvalTools.Exec,
                Arguments =
                [
                    Arg.PathMatches(FakeHomeAssistant.VacuumPathPattern),
                    Arg.Matches("command", @"^set_fan_speed\.sh\b")
                ]
            }
        ],
        Permitted =
        [
            .. CallPermission.LookingAndManuals("/ha*"),
            new CallPermission(EvalTools.Exec, "*aspiradora*")
        ],
        // The matcher above is case-blind; this is not. The mount's parser rejects anything but
        // the listed options byte-for-byte, so the attribute ends at "Turbo" only if a listed
        // spelling was actually sent.
        Changes = [new StateChange($"{FakeHomeAssistant.VacuumEntityId}#fan_speed", "Turbo")],
        CallCeiling = 6,
        Reply = new ReplyExpectation { Spoken = true, MaxSentences = 1 },
        Claims = [HomeAssistantPrompt.ArgumentsComeFromHelp.Id],
        Policy = new RunPolicy(2, 3)
    };

    // The turn arrives with a dismissed *alarm* on it — not a timer — so "five more minutes" is
    // a new one-shot calendar event at the offset, with the same summary and an insistent
    // description. The timers mount is there and unpermitted, which is the discrimination.
    public static Scenario SnoozeTheMedicationAlarm => new()
    {
        Name = "a snoozed alarm is a new calendar event",
        AgentId = "nabu",
        Turn = new EvalTurn
        {
            Text = "cinco minutos más",
            Sender = "fran",
            Room = "kitchen",
            SatelliteId = "kitchen-01",
            DismissedAlert = "alarma de la medicación"
        },
        Instant = EvalInstant.Evening,
        Required =
        [
            new CallExpectation
            {
                Label = "snooze",
                Tool = EvalTools.Exec,
                Arguments =
                [
                    Arg.Path(FakeHomeAssistant.AlarmsDirectory),
                    Arg.Matches("command", @"^create_event\.sh\b"),
                    Arg.Matches("command", @"2026-08-17[ T]20:05"),
                    Arg.Matches("command", "(?i)medicaci"),
                    Arg.Matches("command", "insistent")
                ]
            }
        ],
        Permitted =
        [
            .. CallPermission.LookingAndManuals("/ha*"),
            new CallPermission(EvalTools.Exec, FakeHomeAssistant.AlarmsDirectory)
        ],
        CallCeiling = 6,
        Claims = [HomeAssistantPrompt.SnoozeIsANewEvent.Id],
        Policy = new RunPolicy(2, 3)
    };

    // The written half of the failure rule: the garage has no light, and the reply on a written
    // channel says so in plain words — no exit code, no stderr, no entity id. The spoken half is
    // the voice family's one-clause scenario.
    public static Scenario TheGarageLightInWriting => new()
    {
        Name = "a written failure is plain words with no code",
        AgentId = "jonas",
        Turn = new EvalTurn
        {
            Text = "Apaga la luz del garaje.",
            Sender = "fran"
        },
        Instant = EvalInstant.Evening,
        // Nothing is required: there is no garage light, so the right number of successful
        // actions is none.
        Permitted = [.. CallPermission.Looking("/ha*")],
        CallCeiling = 5,
        Reply = new ReplyExpectation
        {
            Mentions =
            [
                new SpokenValue("that there is none",
                    "no hay", "no existe", "no encuentro", "no veo", "no aparece", "no tengo")
            ],
            NeverSays = ["exit", "127", "stderr", "not_found", "errorCode", "light."]
        },
        Claims = [HomeAssistantPrompt.ExitCodesAreNeverVoiced.Id],
        Policy = new RunPolicy(2, 3)
    };
}