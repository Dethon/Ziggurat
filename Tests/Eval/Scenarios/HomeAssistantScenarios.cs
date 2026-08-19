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
        [TurnTheAirConditionerOn, SetTheTemperature, VacuumTheStudy];

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
}