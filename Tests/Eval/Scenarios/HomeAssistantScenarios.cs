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
        TurnOnTheWashingMachine, TheStationThatCannotBePlayed, FiveMoreMinutesOfTheAlarm,
        CancelTheTrashAlarm, MoveTheTrashAlarm
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
        Guards =
        [
            new Guard(HomeAssistantPrompt.ExactlyWhatWasAsked.Id,
                "Demonstrated on 2026-08-18 with the whole Scope paragraph deleted: 'enciende el aire' "
                + "still turned the air conditioning on and touched nothing else. Doing only what was "
                + "asked is the model's default here, so the scenario runs as a regression guard and the "
                + "state diff it introduced is what actually earns its place.")
        ],
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
        Guards =
        [
            new Guard(HomeAssistantPrompt.ExitCodeIsTheConfirmation.Id,
                "Demonstrated on 2026-08-18 with the never-re-read rule deleted from both places it is "
                + "written: the model set the temperature and stopped. It does not check its own work "
                + "unprompted, so the prose defends against a habit this model does not have.")
        ],
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
        // Two claims on one call: the value of --cleaning_area_id is the frozen slug (the setup
        // index's only bridge between "estudio" and `despacho`), and the flag's *name* exists
        // nowhere but the action's --help output — the index gives slugs, not argument names, so
        // a call carrying the exact flag read it rather than guessing, and the ceiling bounds a
        // guess-and-retry loop. The fix-exit-2-by-re-reading half of the help rule stays
        // unwitnessed until a first attempt can be forced to fail.
        Claims =
        [
            HomeAssistantPrompt.AreaSlugIsReadNotDerived.Id,
            HomeAssistantPrompt.ArgumentsComeFromHelp.Id
        ],
        Policy = new RunPolicy(2, 3)
    };

    // The washing machine answers only to the composed directory name a listing returns. The
    // permitted set is the teeth: only paths carrying the `lavadora_(…)` suffix are tolerated, so
    // a bare id or a guessed suffix fails as an unnecessary call instead of costing a quiet
    // near-miss round-trip.
    public static Scenario TurnOnTheWashingMachine => new()
    {
        Name = "an entity is named exactly as the listing spells it",
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
                    Arg.PathMatches(FakeHomeAssistant.WashingMachinePathPattern),
                    Arg.Matches("command", @"^turn_on\.sh")
                ]
            }
        ],
        Permitted =
        [
            .. CallPermission.Looking("/ha*"),
            new CallPermission(EvalTools.Exec, "*lavadora_(*")
        ],
        Changes = [new StateChange(FakeHomeAssistant.WashingMachineEntityId, "on")],
        CallCeiling = 4,
        Reply = new ReplyExpectation { Spoken = true, MaxSentences = 1 },
        Claims = [HomeAssistantPrompt.EntityNamedAsListed.Id],
        Policy = new RunPolicy(2, 3)
    };

    // A name nothing resolves: Home Assistant answers with a bare 500 that says nothing about
    // what went wrong. The written contract asks for the failure in plain words — no exit code,
    // no stderr text — and the ceiling is where retrying name variants shows.
    public static Scenario TheStationThatCannotBePlayed => new()
    {
        Name = "a failure reaches a written reply in plain words",
        AgentId = "jonas",
        Turn = new EvalTurn
        {
            // A station that does not exist anywhere, deliberately: the first armed run asked
            // for a real one, and the model spent ten calls rewording browses and searches
            // before believing the 500 — persistence a real name half-earns. An invented one
            // makes one confirmation enough.
            Text = "Pon Radio Faro del Sur en el altavoz de la cocina.",
            Sender = "fran"
        },
        Instant = EvalInstant.Evening,
        Required =
        [
            new CallExpectation
            {
                Label = "play",
                Tool = EvalTools.Exec,
                Arguments =
                [
                    Arg.PathMatches(FakeHomeAssistant.KitchenSpeakerPathPattern),
                    Arg.Matches("command", @"^music_assistant\.play_media\.sh\b"),
                    Arg.Matches("command", "(?i)faro")
                ]
            }
        ],
        // The honest recoveries, and nothing else: the library listing (which holds no radio),
        // and the provider-catalog search. A bare play_media.sh is not among them.
        Permitted =
        [
            .. CallPermission.LookingAndManuals("/ha*"),
            new CallPermission(EvalTools.Exec, "*altavoz*", "*browse_media*"),
            new CallPermission(EvalTools.Exec, "*altavoz*", "*search_media*")
        ],
        // Loose on purpose: three armed rounds showed this model's floor for believing a media
        // failure is nine calls — one play, then helps, browses and searches, never a playback
        // retry — which is the contract's own "list the real items" done thoroughly. The claim
        // cited here is about the reply; playback-retry storms are what FavouriteMusic's tight
        // ceiling guards.
        CallCeiling = 10,
        Reply = new ReplyExpectation
        {
            Mentions = [new SpokenValue("what could not be played", "Faro del Sur", "emisora")],
            NeverSays = ["500", "Internal Server Error", "stderr", "exit"]
        },
        // The invented station is also the temptation the web-fallback rule forbids: with
        // nothing in the library to play, going looking online is the next thing a helpful
        // model reaches for — the 2026-08-20 full pass showed it searching for the station's
        // stream twice — and the exhaustive permitted set is what fails it.
        Claims =
        [
            HomeAssistantPrompt.ExitCodesAreNeverVoiced.Id,
            HomeAssistantPrompt.TheWebIsNoMediaFallback.Id
        ],
        Policy = new RunPolicy(2, 4)
    };

    // A dismissed calendar alarm and a request for five more minutes: the snooze is a new
    // one-shot event at the offset, with the same errand and still insistent — a snooze that
    // announces once and gives up is not the alarm the user silenced.
    public static Scenario FiveMoreMinutesOfTheAlarm => new()
    {
        Name = "a snooze is a new event with the same summary",
        AgentId = "nabu",
        Turn = new EvalTurn
        {
            Text = "cinco minutos más y me lo recuerdas",
            Sender = "fran",
            Room = "kitchen",
            SatelliteId = "kitchen-01",
            // The exact shape the voice channel writes into the turn: {kind} "{text}".
            DismissedAlert = "alarm \"Sacar la basura\""
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
                    Arg.PathMatches(FakeHomeAssistant.AlarmsPathPattern),
                    Arg.Matches("command", @"^create_event\.sh\b"),
                    Arg.Matches("command", @"2026-08-17[ T]20:05"),
                    Arg.Matches("command", "(?i)basura"),
                    Arg.Matches("command", "(?i)insistent")
                ]
            }
        ],
        Permitted =
        [
            .. CallPermission.LookingAndManuals("/ha*"),
            .. CallPermission.Looking("/timers*"),
            new CallPermission(EvalTools.Exec, "*assistant_alarms_(*")
        ],
        CallCeiling = 6,
        Changes = [new StateChange(FakeHomeAssistant.AlarmsEventCountKey, "2")],
        Claims = [HomeAssistantPrompt.SnoozeIsANewEvent.Id, BasePrompt.NoPlaceholderToolCalls.Id],
        Policy = new RunPolicy(2, 3)
    };

    // An alarm that exists is cancelled by its uid: listed first, because nothing else names it,
    // then deleted. The failure this pins is one a real turn showed — an agent that found no way
    // to delete created a second event as a way of "changing" the first, and the calendar held
    // both.
    public static Scenario CancelTheTrashAlarm => new()
    {
        Name = "an alarm is cancelled by its uid",
        AgentId = "nabu",
        Turn = new EvalTurn
        {
            Text = "quita la alarma de sacar la basura",
            Sender = "fran",
            Room = "kitchen",
            SatelliteId = "kitchen-01"
        },
        Instant = EvalInstant.Evening,
        Required =
        [
            new CallExpectation
            {
                Label = "list",
                Tool = EvalTools.Exec,
                Arguments =
                [
                    Arg.PathMatches(FakeHomeAssistant.AlarmsPathPattern),
                    Arg.Matches("command", @"^get_events\.sh\b")
                ]
            },
            new CallExpectation
            {
                Label = "delete",
                Tool = EvalTools.Exec,
                Arguments =
                [
                    Arg.PathMatches(FakeHomeAssistant.AlarmsPathPattern),
                    Arg.Matches("command", @"^delete_event\.sh\b"),
                    Arg.Matches("command", $@"--uid[= ]+""?{FakeHomeAssistant.TrashAlarmUid}\b")
                ]
            }
        ],
        Permitted =
        [
            .. CallPermission.LookingAndManuals("/ha*"),
            new CallPermission(EvalTools.Exec, "*assistant_alarms_(*")
        ],
        CallCeiling = 6,
        Changes = [new StateChange(FakeHomeAssistant.AlarmsEventCountKey, "0")],
        Claims = [HomeAssistantPrompt.AlarmIsCancelledByUid.Id],
        Policy = new RunPolicy(2, 3)
    };

    // Moving an alarm is the turn the real failure came from: with no delete, the agent created a
    // second event at the new time and reported success. The calendar must end with one event —
    // the count is the fact a second event would betray — at the new time, with the old one gone
    // by its uid.
    public static Scenario MoveTheTrashAlarm => new()
    {
        Name = "an alarm is moved by deleting it and creating the new one",
        AgentId = "nabu",
        Turn = new EvalTurn
        {
            Text = "cambia la alarma de sacar la basura a las diez y media",
            Sender = "fran",
            Room = "kitchen",
            SatelliteId = "kitchen-01"
        },
        Instant = EvalInstant.Evening,
        Required =
        [
            new CallExpectation
            {
                Label = "delete",
                Tool = EvalTools.Exec,
                Arguments =
                [
                    Arg.PathMatches(FakeHomeAssistant.AlarmsPathPattern),
                    Arg.Matches("command", @"^delete_event\.sh\b"),
                    Arg.Matches("command", $@"--uid[= ]+""?{FakeHomeAssistant.TrashAlarmUid}\b")
                ]
            },
            new CallExpectation
            {
                Label = "create",
                Tool = EvalTools.Exec,
                Arguments =
                [
                    Arg.PathMatches(FakeHomeAssistant.AlarmsPathPattern),
                    Arg.Matches("command", @"^create_event\.sh\b"),
                    Arg.Matches("command", @"2026-08-17[ T]22:30"),
                    Arg.Matches("command", "(?i)basura"),
                    Arg.Matches("command", "(?i)insistent")
                ]
            }
        ],
        Permitted =
        [
            .. CallPermission.LookingAndManuals("/ha*"),
            new CallPermission(EvalTools.Exec, "*assistant_alarms_(*")
        ],
        CallCeiling = 8,
        // Declared as unchanged on purpose: one event before, one after. A create without the
        // delete leaves two, and the diff reports it.
        Claims = [HomeAssistantPrompt.AlarmIsChangedByDeleteAndCreate.Id, HomeAssistantPrompt.AlarmIsCancelledByUid.Id],
        Policy = new RunPolicy(2, 3)
    };
}