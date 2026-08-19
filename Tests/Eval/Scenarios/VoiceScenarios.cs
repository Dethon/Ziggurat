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
        TheLikeliestLightIsActedOn, TheTemperatureIsSpelledOut, SlowWorkOpensWithOneWord
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
        Claims = [VoicePrompt.FailureIsOneClause.Id, VoicePrompt.NothingUnspeakable.Id],
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

    // "La luz", with no room named and nothing destroyed by being wrong: the likeliest reading is
    // the speaking room's light, and the contract says act on it rather than ask. The question a
    // careful model wants to ask is exactly the failure.
    public static Scenario TheLikeliestLightIsActedOn => new()
    {
        Name = "an unclear request is acted on, not asked about",
        AgentId = "nabu",
        Turn = new EvalTurn
        {
            Text = "apaga la luz",
            Sender = "fran",
            Room = "kitchen",
            SatelliteId = "kitchen-01"
        },
        Instant = EvalInstant.Evening,
        Required =
        [
            new CallExpectation
            {
                Label = "turn off",
                Tool = EvalTools.Exec,
                Arguments =
                [
                    Arg.Path(FakeHomeAssistant.KitchenLightDirectory),
                    Arg.Matches("command", @"^turn_off\.sh")
                ]
            }
        ],
        Permitted =
        [
            .. CallPermission.Looking("/ha*"),
            new CallPermission(EvalTools.Exec, FakeHomeAssistant.KitchenLightDirectory)
        ],
        Changes = [new StateChange(FakeHomeAssistant.KitchenLightEntityId, "off")],
        CallCeiling = 5,
        Reply = new ReplyExpectation
        {
            Spoken = true,
            MaxSentences = 1,
            NeverSays = ["cuál", "qué luz"]
        },
        Claims = [VoicePrompt.UnclearRequestIsActedOn.Id],
        Policy = new RunPolicy(2, 3)
    };

    // The answer is a unit, which a screen writes as a symbol and a voice has to say in words.
    // The value is an attribute the turn did not change, which is the one legal reason to read
    // state.json.
    public static Scenario TheTemperatureIsSpelledOut => new()
    {
        Name = "a spoken unit is spelled out",
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
                Label = "read",
                Tool = EvalTools.Read,
                Arguments = [Arg.PathMatches(@"state\.json$")]
            }
        ],
        Permitted = [.. CallPermission.Looking("/ha*")],
        CallCeiling = 5,
        Reply = new ReplyExpectation
        {
            Spoken = true,
            MaxSentences = 1,
            Mentions =
            [
                new SpokenValue("the temperature", "veinticuatro", "24"),
                new SpokenValue("the unit, in words", "grados")
            ],
            NeverSays = ["°"]
        },
        Claims = [VoicePrompt.AbbreviationsAreSpelledOut.Id],
        Policy = new RunPolicy(2, 3)
    };

    // A spoken turn whose work is a search: the contract's opener applies — one plain word,
    // once, then nothing until the answer. Only the reply's shape is checkable here; that the
    // word reached the speaker before the tools ran is the channel's timing, out of a
    // recording's reach.
    public static Scenario SlowWorkOpensWithOneWord => new()
    {
        Name = "slow spoken work opens with one word",
        AgentId = "nabu",
        Turn = new EvalTurn
        {
            Text = "busca a qué hora abre el museo del Carmen",
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
                Arguments = [Arg.Matches("url", "/museo/horarios")]
            }
        ],
        Permitted =
        [
            new CallPermission(EvalTools.WebSearch),
            new CallPermission(EvalTools.WebBrowse)
        ],
        CallCeiling = 4,
        Reply = new ReplyExpectation
        {
            Spoken = true,
            MaxSentences = 2,
            OpensWithAcknowledgement = true,
            Mentions = [new SpokenValue("the opening time", "diez y media", "10:30", "10.30")]
        },
        Claims = [VoicePrompt.OneWordBeforeSlowWork.Id],
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
}