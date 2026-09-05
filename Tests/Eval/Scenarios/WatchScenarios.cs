using Domain.Prompts;
using Tests.Eval.Fixtures;
using Tests.Eval.Harness;

namespace Tests.Eval.Scenarios;

// Reacting to the home: a watch is a file under /ha/watches that becomes a real automation, and
// what these assert is the automation the fake home received — its effect kind, its triggers, its
// delivery — and the watch count moving or holding still. The fake home starts with one watch
// (Laura's sugar below 70, on Telegram), so creating shows as one becoming two and changing,
// pausing or removing act on that one.
public static class WatchScenarios
{
    public static IReadOnlyList<Scenario> All =>
    [
        CloseTheBlindsWhenHot, WakeMeIfHerSugarDrops, WarnMeOnTelegram, WarnMeByVoice, WarnMeOutsideARange,
        AnUrgentWarning, TellMeWhenTheWashingMachineFinishes, ChangeTheThreshold, NotTonight, RemoveTheWatch
    ];

    private const string WatchFile = @"^/ha/watches/[^/]+/watch\.json$";

    private static IReadOnlyList<CallPermission> LookingAtTheHome =>
        [.. CallPermission.LookingAndManuals("/ha*")];

    private static string Kind(string kind) => $@"""kind""\s*:\s*""{kind}""";

    // Creating a watch beside the seeded one: the count becomes two, and a new automation is on.
    private static IReadOnlyList<StateChange> OneMoreWatch =>
    [
        new StateChange(FakeHomeAssistant.WatchCountKey, "2"),
        new StateChange(FakeHomeAssistant.AnyAutomationEntity, "on")
    ];

    // A home action: the blinds close when the living room passes 27, with nobody in the loop. The
    // effect is Home Assistant's own action JSON and carries no prompt for the agent.
    public static Scenario CloseTheBlindsWhenHot => new()
    {
        Name = "a home action watch closes the blinds without an agent",
        AgentId = "nabu",
        Turn = new EvalTurn
        {
            Text = "cierra las persianas del salón cuando la temperatura del salón pase de 27 grados",
            Sender = "fran",
            Room = "kitchen",
            SatelliteId = "kitchen-01"
        },
        Instant = EvalInstant.Evening,
        Required =
        [
            new CallExpectation
            {
                Label = "watch",
                Tool = EvalTools.Create,
                Arguments =
                [
                    Arg.PathMatches(WatchFile),
                    Arg.Matches("content", Kind("actions")),
                    Arg.Matches("content", @"cover\.close_cover"),
                    Arg.Matches("content", FakeHomeAssistant.SalonBlindsEntityId),
                    Arg.Matches("content", FakeHomeAssistant.SalonTemperatureEntityId),
                    Arg.Matches("content", @"""above""\s*:\s*27"),
                    Arg.Lacks("content", Kind("prompt"))
                ]
            }
        ],
        Permitted = LookingAtTheHome,
        Changes = OneMoreWatch,
        CallCeiling = 6,
        Reply = new ReplyExpectation { Spoken = true, MaxSentences = 2 },
        Claims =
        [
            HomeAssistantPrompt.ReactingToTheHomeIsAWatch.Id,
            HomeAssistantPrompt.WatchHomeActionIsAnActionsEffect.Id
        ],
        Judged =
        [
            new JudgedCheck(HomeAssistantPrompt.WatchIsReadBackOnCreation.Id,
                "The user asked for the living-room blinds to close when the living-room temperature "
                + "passes 27 degrees, and the assistant created a watch for it. Judge the spoken reply "
                + "alone. Pass if it reads the watch back: it names the blinds (or the living room) and "
                + "the threshold (27, or 'veintisiete'), so the user could catch a misunderstanding. "
                + "Fail a bare 'done' / 'hecho' / 'listo' that names neither, or a reply that describes "
                + "something other than what was created.")
        ],
        Policy = new RunPolicy(2, 3),
        Tier = EvalTier.Smoke
    };

    // Urgency, at night, in the room the request came from: an insistent announcement rings until
    // it is acknowledged, with no agent involved, and a time condition keeps it to the night.
    public static Scenario WakeMeIfHerSugarDrops => new()
    {
        Name = "a night-time warning is an insistent announcement with a condition",
        AgentId = "nabu",
        Turn = new EvalTurn
        {
            Text = "si el azúcar de Laura baja de 55 por la noche despiértame aquí, que suene hasta que lo apague",
            Sender = "fran",
            Room = "office",
            SatelliteId = "office-01"
        },
        Instant = EvalInstant.Evening,
        Required =
        [
            new CallExpectation
            {
                Label = "watch",
                Tool = EvalTools.Create,
                Arguments =
                [
                    Arg.PathMatches(WatchFile),
                    Arg.Matches("content", Kind("announce")),
                    Arg.Matches("content", @"""insistent"""),
                    Arg.Matches("content", @"""below""\s*:\s*55"),
                    Arg.Matches("content", FakeHomeAssistant.GlucoseEntityId),
                    Arg.Matches("content", @"""condition""\s*:\s*""time"""),
                    Arg.Matches("content", @"office")
                ]
            }
        ],
        Permitted = LookingAtTheHome,
        Changes = OneMoreWatch,
        CallCeiling = 6,
        Reply = new ReplyExpectation { Spoken = true, MaxSentences = 2 },
        Claims = [HomeAssistantPrompt.WatchUrgencyIsAnInsistentAnnouncement.Id],
        Policy = new RunPolicy(2, 3)
    };

    // The plain case on Telegram: a prompt effect, no announcement, and no delivery named — the
    // mount sends the answer where the request came from, so a `deliverTo` the model invents is
    // the only thing that could send it elsewhere.
    public static Scenario WarnMeOnTelegram => new()
    {
        Name = "a plain warning on telegram is a prompt watch",
        AgentId = "jonas",
        Turn = new EvalTurn
        {
            Text = "Avísame cuando el azúcar de Laura pase de 180.",
            Sender = "fran",
            Channel = "telegram"
        },
        Instant = EvalInstant.Evening,
        Required =
        [
            new CallExpectation
            {
                Label = "watch",
                Tool = EvalTools.Create,
                Arguments =
                [
                    Arg.PathMatches(WatchFile),
                    Arg.Matches("content", Kind("prompt")),
                    Arg.Matches("content", @"""above""\s*:\s*180"),
                    Arg.Matches("content", FakeHomeAssistant.GlucoseEntityId),
                    Arg.Lacks("content", Kind("announce")),
                    Arg.Lacks("content", @"""(voice|signalr)")
                ]
            }
        ],
        Permitted = LookingAtTheHome,
        Changes = OneMoreWatch,
        CallCeiling = 6,
        Claims =
        [
            HomeAssistantPrompt.ReactingToTheHomeIsAWatch.Id,
            HomeAssistantPrompt.WatchDefaultsToAPromptDeliveredWhereAsked.Id
        ],
        Policy = new RunPolicy(2, 3)
    };

    // The same request by voice: still a prompt watch, still delivered where it was asked — and
    // Nabu has no Telegram, so a delivery naming it is the failure this one exists to catch.
    public static Scenario WarnMeByVoice => new()
    {
        Name = "a plain warning by voice is a prompt watch that never names telegram",
        AgentId = "nabu",
        Turn = new EvalTurn
        {
            Text = "avísame cuando el azúcar de Laura pase de 180",
            Sender = "fran",
            Room = "kitchen",
            SatelliteId = "kitchen-01"
        },
        Instant = EvalInstant.Evening,
        Required =
        [
            new CallExpectation
            {
                Label = "watch",
                Tool = EvalTools.Create,
                Arguments =
                [
                    Arg.PathMatches(WatchFile),
                    Arg.Matches("content", Kind("prompt")),
                    Arg.Matches("content", @"""above""\s*:\s*180"),
                    Arg.Matches("content", FakeHomeAssistant.GlucoseEntityId),
                    Arg.Lacks("content", Kind("announce")),
                    Arg.Lacks("content", "telegram")
                ]
            }
        ],
        Permitted = LookingAtTheHome,
        Changes = OneMoreWatch,
        CallCeiling = 6,
        Reply = new ReplyExpectation { Spoken = true, MaxSentences = 2 },
        Claims =
        [
            HomeAssistantPrompt.WatchDefaultsToAPromptDeliveredWhereAsked.Id,
            HomeAssistantPrompt.NabuNeverDeliversToTelegram.Id
        ],
        Policy = new RunPolicy(2, 3)
    };

    // A range is one watch with two triggers. Two watches would show as the count reaching three.
    public static Scenario WarnMeOutsideARange => new()
    {
        Name = "a range to leave is one watch with two triggers",
        AgentId = "jonas",
        Turn = new EvalTurn
        {
            Text = "Avísame si la temperatura del salón se sale del rango de 18 a 26 grados.",
            Sender = "fran"
        },
        Instant = EvalInstant.Evening,
        Required =
        [
            new CallExpectation
            {
                Label = "watch",
                Tool = EvalTools.Create,
                Arguments =
                [
                    Arg.PathMatches(WatchFile),
                    Arg.Matches("content", @"""below""\s*:\s*18"),
                    Arg.Matches("content", @"""above""\s*:\s*26"),
                    Arg.Matches("content", FakeHomeAssistant.SalonTemperatureEntityId)
                ]
            }
        ],
        Permitted = LookingAtTheHome,
        Changes = OneMoreWatch,
        CallCeiling = 6,
        Claims = [HomeAssistantPrompt.WatchRangeIsTwoTriggers.Id],
        Policy = new RunPolicy(2, 3)
    };

    // Both effects on one watch: the kitchen rings now, the reasoned message follows.
    public static Scenario AnUrgentWarning => new()
    {
        Name = "an urgent warning rings insistently and then prompts the agent",
        AgentId = "jonas",
        Turn = new EvalTurn
        {
            Text = "Si el azúcar de Laura baja de 50, que suene una alarma insistente en la cocina y mándame aquí los detalles.",
            Sender = "fran",
            Channel = "telegram"
        },
        Instant = EvalInstant.Evening,
        Required =
        [
            new CallExpectation
            {
                Label = "watch",
                Tool = EvalTools.Create,
                Arguments =
                [
                    Arg.PathMatches(WatchFile),
                    Arg.Matches("content", Kind("announce")),
                    Arg.Matches("content", @"""insistent"""),
                    Arg.Matches("content", Kind("prompt")),
                    Arg.Matches("content", @"""below""\s*:\s*50"),
                    Arg.Matches("content", "kitchen")
                ]
            }
        ],
        Permitted = LookingAtTheHome,
        Changes = OneMoreWatch,
        CallCeiling = 6,
        Claims = [HomeAssistantPrompt.WatchUrgencyIsAnInsistentAnnouncement.Id],
        Policy = new RunPolicy(2, 3)
    };

    // "Tell me when it finishes" is once: the watch turns itself off after the first fire rather
    // than announcing every cycle for the rest of time.
    public static Scenario TellMeWhenTheWashingMachineFinishes => new()
    {
        Name = "tell me when the washing machine finishes is a one-shot watch",
        AgentId = "nabu",
        Turn = new EvalTurn
        {
            Text = "avísame cuando termine la lavadora",
            Sender = "fran",
            Room = "kitchen",
            SatelliteId = "kitchen-01"
        },
        Instant = EvalInstant.Evening,
        Required =
        [
            new CallExpectation
            {
                Label = "watch",
                Tool = EvalTools.Create,
                Arguments =
                [
                    Arg.PathMatches(WatchFile),
                    Arg.Matches("content", @"""once""\s*:\s*true"),
                    Arg.Matches("content", FakeHomeAssistant.WashingMachineEntityId),
                    Arg.Matches("content", @"""to""\s*:\s*""off""")
                ]
            }
        ],
        Permitted = LookingAtTheHome,
        Changes = OneMoreWatch,
        CallCeiling = 6,
        Reply = new ReplyExpectation { Spoken = true, MaxSentences = 2 },
        Claims = [HomeAssistantPrompt.WatchOneShotUsesOnce.Id],
        Policy = new RunPolicy(2, 3)
    };

    // The failure alarms had: a change that ends as two records. The seeded watch is edited under
    // its own id and the count stays at one.
    public static Scenario ChangeTheThreshold => new()
    {
        Name = "a threshold change edits the same watch",
        AgentId = "jonas",
        Turn = new EvalTurn
        {
            Text = "Cambia el aviso del azúcar baja de Laura: que avise por debajo de 65, no de 70.",
            Sender = "fran",
            Channel = "telegram"
        },
        Instant = EvalInstant.Evening,
        Required =
        [
            new CallExpectation
            {
                Label = "edit",
                Tool = EvalTools.Edit,
                Arguments =
                [
                    Arg.PathMatches(FakeHomeAssistant.SugarWatchPathPattern),
                    Arg.Mentions("edits", @"65")
                ]
            }
        ],
        Permitted =
        [
            .. LookingAtTheHome,
            new CallPermission(EvalTools.Create, $"/ha/watches/{FakeHomeAssistant.SugarWatchId}/*")
        ],
        CallCeiling = 6,
        Claims = [HomeAssistantPrompt.WatchChangeReplacesInPlace.Id],
        Policy = new RunPolicy(2, 3)
    };

    // A pause is enabled false on the existing watch: the automation goes off and the count holds.
    public static Scenario NotTonight => new()
    {
        Name = "stop warning me tonight pauses the watch",
        AgentId = "jonas",
        Turn = new EvalTurn
        {
            Text = "Esta noche no me avises del azúcar baja de Laura, ya lo reactivo yo mañana.",
            Sender = "fran",
            Channel = "telegram"
        },
        Instant = EvalInstant.Evening,
        Required =
        [
            new CallExpectation
            {
                Label = "pause",
                Tool = EvalTools.Edit,
                Arguments =
                [
                    Arg.PathMatches(FakeHomeAssistant.SugarWatchPathPattern),
                    Arg.Mentions("edits", @"\\""enabled\\""\s*:\s*false")
                ]
            }
        ],
        Permitted =
        [
            .. LookingAtTheHome,
            new CallPermission(EvalTools.Create, $"/ha/watches/{FakeHomeAssistant.SugarWatchId}/*")
        ],
        Changes = [new StateChange(FakeHomeAssistant.SugarWatchEntityId, "off")],
        CallCeiling = 6,
        Claims = [HomeAssistantPrompt.WatchPauseIsEnabledFalse.Id],
        Policy = new RunPolicy(2, 3)
    };

    public static Scenario RemoveTheWatch => new()
    {
        Name = "removing a watch deletes it from the home",
        AgentId = "jonas",
        Turn = new EvalTurn
        {
            Text = "Quita el aviso del azúcar baja de Laura.",
            Sender = "fran",
            Channel = "telegram"
        },
        Instant = EvalInstant.Evening,
        Required =
        [
            new CallExpectation
            {
                Label = "delete",
                Tool = EvalTools.Remove,
                Arguments = [Arg.PathMatches(FakeHomeAssistant.SugarWatchPathPattern)]
            }
        ],
        Permitted = LookingAtTheHome,
        Changes = [new StateChange(FakeHomeAssistant.WatchCountKey, "0")],
        CallCeiling = 5,
        Claims = [HomeAssistantPrompt.WatchIsRemovedByDelete.Id],
        Policy = new RunPolicy(2, 3)
    };
}