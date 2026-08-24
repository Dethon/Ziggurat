using Domain.Prompts;
using Tests.Eval.Fixtures;
using Tests.Eval.Harness;

namespace Tests.Eval.Scenarios;

// What the agent said, as scenarios. Every reply here is spoken aloud by a text-to-speech engine,
// so the contract is about the shape of the answer: how long it is, whether the value the user
// said survived into it, and whether it carries anything that cannot be pronounced.
public static class VoiceScenarios
{
    // Eight minutes armed and three of them spent, so the remaining five exists only in
    // status.json — the same seed the extend scenario uses, for the same reason.
    private static readonly ArmedTimerSeed _pasta =
        new("pasta", DurationSeconds: 480, Room: "kitchen", RunningFor: TimeSpan.FromMinutes(3));

    public static IReadOnlyList<Scenario> All =>
    [
        ActionConfirmedWithItsValue, TimeLeftIsTheValueAlone, WhatDoesNotExistIsOneClause,
        StopTheRingingAlarm, TwoMoreMinutesAfterItRang, WhenItFiresInWriting,
        FiveMinutesForTea, TheThermostatInWords, TheRaffleByVoice
    ];

    // The reply to an action: two words, and the number the user said back in them. Repeating the
    // value is what makes a two-word confirmation safe — the user hears which request was heard.
    public static Scenario ActionConfirmedWithItsValue => new()
    {
        Name = "an action is confirmed in a few words carrying its value",
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
                Arguments = [Arg.PathMatches(@"^/timers/[^/]+/timer\.json$")]
            }
        ],
        Permitted = [.. CallPermission.Looking("/timers*")],
        CallCeiling = 4,
        Reply = new ReplyExpectation
        {
            MaxSentences = 1,
            // The contract says twelve and excludes spelled-out numbers and units from the count;
            // this check counts every word, so the declared limit is the contract's plus the two
            // or three words that exclusion is worth. A stricter number here would fail replies
            // the contract allows.
            MaxWords = 15,
            Spoken = true,
            Mentions = [new SpokenValue("the duration the user said", "ocho", "8")]
        },
        Claims = [VoicePrompt.ActionConfirmedInTwoWords.Id, VoicePrompt.ValueSaidIsRepeated.Id],
        Policy = new RunPolicy(2, 3),
        Tier = EvalTier.Smoke
    };

    // A fact asked for, answered with the value alone. The firing time is in the same file and is
    // exactly the sort of extra a screen-oriented reply would add.
    public static Scenario TimeLeftIsTheValueAlone => new()
    {
        Name = "time left is answered with the value alone",
        AgentId = "nabu",
        Turn = new EvalTurn
        {
            Text = "¿cuánto queda del temporizador de la pasta?",
            Sender = "fran",
            Room = "kitchen",
            SatelliteId = "kitchen-01"
        },
        Instant = EvalInstant.Evening,
        Armed = [_pasta],
        Required =
        [
            new CallExpectation
            {
                Label = "status",
                Tool = EvalTools.Read,
                Arguments = [Arg.Path("/timers/pasta/status.json")]
            }
        ],
        Permitted = [.. CallPermission.Looking("/timers*")],
        CallCeiling = 4,
        Reply = new ReplyExpectation
        {
            MaxSentences = 1,
            MaxWords = 15,
            Spoken = true,
            Mentions = [new SpokenValue("the remaining time", "cinco", "5")],
            // 20:05 is when it fires, and a spoken answer does not carry it.
            NeverSays = ["20:05", "8:05", "20.05"]
        },
        Claims =
        [
            VoicePrompt.FactIsTheValueAlone.Id,
            TimerPrompt.SpokenStatusGivesOnlyTheRemainingTime.Id
        ],
        Policy = new RunPolicy(2, 3)
    };

    // Something that does not exist, stated in one clause. The temptation here is the exec's own
    // words: an exit code and a stderr line naming entity ids, read out loud.
    public static Scenario WhatDoesNotExistIsOneClause => new()
    {
        Name = "what does not exist is one clause with no code",
        AgentId = "nabu",
        Turn = new EvalTurn
        {
            Text = "apaga la luz del garaje",
            Sender = "fran",
            Room = "office",
            SatelliteId = "office-01"
        },
        Instant = EvalInstant.Evening,
        // Nothing is required: the garage has no light, so the right number of successful actions
        // is none, and what is being checked is what the agent said about that.
        Permitted = [.. CallPermission.Looking("/ha*")],
        CallCeiling = 6,
        Reply = new ReplyExpectation
        {
            MaxSentences = 1,
            MaxWords = 15,
            Spoken = true,
            NeverSays = ["exit", "127", "stderr", "código"]
        },
        Claims =
        [
            VoicePrompt.FailureIsOneClause.Id, VoicePrompt.NothingUnspeakable.Id,
            BasePrompt.NoPlaceholderToolCalls.Id
        ],
        Policy = new RunPolicy(2, 3)
    };

    // A timer that is ringing right now is not under /timers any more, so cancelling it by path is
    // impossible: the one way to silence it is the mount's own dismiss action.
    public static Scenario StopTheRingingAlarm => new()
    {
        Name = "a ringing alarm is silenced by dismissing it",
        AgentId = "nabu",
        Turn = new EvalTurn
        {
            Text = "para la alarma que está sonando",
            Sender = "fran",
            Room = "kitchen",
            SatelliteId = "kitchen-01"
        },
        Instant = EvalInstant.Evening,
        Required =
        [
            new CallExpectation
            {
                Label = "dismiss",
                Tool = EvalTools.Exec,
                Arguments = [Arg.Path("/timers"), Arg.Matches("command", @"^\.?/?dismiss\.sh")]
            }
        ],
        Permitted = [.. CallPermission.Looking("/timers*")],
        CallCeiling = 4,
        Claims = [TimerPrompt.RingingIsStoppedByDismiss.Id],
        Policy = new RunPolicy(2, 3)
    };

    // "Two more minutes" about a timer that already fired and was dismissed. There is nothing left
    // to extend — the old timer is gone — so the answer is a new one, and a model looking for the
    // old one to read or delete is chasing a file that no longer exists.
    public static Scenario TwoMoreMinutesAfterItRang => new()
    {
        Name = "two more minutes after it rang is a new timer",
        AgentId = "nabu",
        Turn = new EvalTurn
        {
            Text = "dos minutos más",
            Sender = "fran",
            Room = "kitchen",
            SatelliteId = "kitchen-01",
            DismissedAlert = "pasta timer"
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
                    Arg.Body("content", Arg.Number("durationSeconds", 120))
                ]
            }
        ],
        Permitted = [.. CallPermission.Looking("/timers*")],
        CallCeiling = 4,
        Claims = [TimerPrompt.ExtendingADismissedOneIsANewTimer.Id],
        Policy = new RunPolicy(2, 3)
    };

    // The same question on a channel that is read rather than heard, where the firing time is what
    // the user asked for and leaving it out is the failure.
    public static Scenario WhenItFiresInWriting => new()
    {
        Name = "a written answer about a timer says when it fires",
        AgentId = "jonas",
        Turn = new EvalTurn
        {
            Text = "¿a qué hora suena el temporizador de la pasta?",
            Sender = "fran"
        },
        Instant = EvalInstant.Evening,
        Armed = [_pasta],
        Required =
        [
            new CallExpectation
            {
                Label = "status",
                Tool = EvalTools.Read,
                Arguments = [Arg.Path("/timers/pasta/status.json")]
            }
        ],
        Permitted = [.. CallPermission.Looking("/timers*")],
        CallCeiling = 4,
        Reply = new ReplyExpectation
        {
            Mentions = [new SpokenValue("the firing time", "20:05", "8:05")]
        },
        Claims = [TimerPrompt.WrittenStatusIncludesFiresAt.Id],
        Policy = new RunPolicy(2, 3)
    };

    // No verb, no "timer", just a duration and a subject. The likeliest reading is the only
    // sensible one, and the contract says to take it and act: a model that asks what was meant
    // never creates anything, and the required call says so.
    public static Scenario FiveMinutesForTea => new()
    {
        Name = "an unclear request is acted on, not asked about",
        AgentId = "nabu",
        Turn = new EvalTurn
        {
            Text = "cinco minutos para el té",
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
                    Arg.Body("content", Arg.Number("durationSeconds", 300))
                ]
            }
        ],
        Permitted = [.. CallPermission.Looking("/timers*")],
        CallCeiling = 3,
        Reply = new ReplyExpectation
        {
            Spoken = true,
            MaxSentences = 1,
            MaxWords = 15,
            Mentions = [new SpokenValue("the duration", "cinco", "5")]
        },
        Claims = [VoicePrompt.UnclearRequestIsActedOn.Id],
        Policy = new RunPolicy(2, 3)
    };

    // The answer is a number with a unit, and state.json spells that unit as a symbol no
    // text-to-speech engine says right. The reply has to carry the unit as a word.
    public static Scenario TheThermostatInWords => new()
    {
        Name = "a unit is spelled out rather than read as a symbol",
        AgentId = "nabu",
        Turn = new EvalTurn
        {
            Text = "¿a qué temperatura está puesto el aire del salón?",
            Sender = "fran",
            Room = "kitchen",
            SatelliteId = "kitchen-01"
        },
        Instant = EvalInstant.Evening,
        Required =
        [
            new CallExpectation
            {
                Label = "state",
                Tool = EvalTools.Read,
                Arguments =
                [
                    Arg.PathMatches(FakeHomeAssistant.AirConditionerPathPattern),
                    Arg.PathMatches(@"state\.json$")
                ]
            }
        ],
        Permitted = [.. CallPermission.Looking("/ha*")],
        CallCeiling = 4,
        Reply = new ReplyExpectation
        {
            Spoken = true,
            MaxSentences = 1,
            MaxWords = 15,
            Mentions =
            [
                new SpokenValue("the target temperature", "veinticuatro", "24"),
                new SpokenValue("the unit as a word", "grados")
            ],
            NeverSays = ["°", "º"]
        },
        Claims = [VoicePrompt.AbbreviationsAreSpelledOut.Id],
        Policy = new RunPolicy(2, 3)
    };

    // Research by voice: a search, a long page, a second fetch for its tail — unambiguously slow
    // work, so the first output must be one plain word, spoken before the tools run. The recording
    // sees it: the pre-tool text and the answer arrive as one reply string.
    public static Scenario TheRaffleByVoice => new()
    {
        Name = "slow work opens with one word and then the answer",
        AgentId = "nabu",
        Turn = new EvalTurn
        {
            Text = "busca en la web del Cuaderno de barrio cuánto recaudó la rifa solidaria "
                   + "de las fiestas",
            Sender = "fran",
            Room = "kitchen",
            SatelliteId = "kitchen-01"
        },
        Instant = EvalInstant.Evening,
        Required =
        [
            new CallExpectation
            {
                Label = "open",
                Tool = EvalTools.WebBrowse,
                Arguments = [Arg.Matches("url", "/cronica/fiestas")]
            }
        ],
        Permitted =
        [
            new CallPermission(EvalTools.WebSearch),
            new CallPermission(EvalTools.WebBrowse)
        ],
        // One worker tolerated, not required: the delegation reflex reaches research phrasing,
        // and a canned worker reads no page, so the parent still pays for the tail itself.
        MayDelegateTo = ["jonas-worker"],
        CallCeiling = 6,
        Reply = new ReplyExpectation
        {
            Spoken = true,
            AcknowledgesFirst = true,
            // The acknowledgement does not count against the limit; the answer itself is bounded.
            MaxSentences = 2,
            Mentions =
            [
                new SpokenValue("the raffle total",
                    "1.842", "1842", "1 842", "mil ochocientos cuarenta y dos")
            ]
        },
        Claims = [VoicePrompt.OneWordBeforeSlowWork.Id],
        Policy = new RunPolicy(2, 4)
    };
}