using Domain.Prompts;
using Tests.Eval.Harness;

namespace Tests.Eval.Scenarios;

// Whether to hand the work to somebody else. What is under test is the parent's decision and the
// prompt it wrote, never the worker's answer — the workers are canned, so a scenario here cannot
// fail on what a second model happened to say.
//
// Whether the research-shaped turn delegates turned out to be a per-run coin (see the exemption
// on `subagents.heavy-work-is-delegated`), so the positive half asserts the outcome and the
// single spend rather than the choice. The two-workers-in-parallel case stays withdrawn: see the
// exemption on `subagents.parallel-parts-are-delegated`.
public static class DelegationScenarios
{
    public static IReadOnlyList<Scenario> All =>
        [OneLookupIsDoneInPlace, TheSummaryStaysHome, TheResearchIsPaidForOnce];

    // One read answers this, and a worker would make it slower and no better. Declaring no
    // delegation is how the scenario says so: any worker at all fails it.
    public static Scenario OneLookupIsDoneInPlace => new()
    {
        Name = "a one-call lookup is not handed to a worker",
        AgentId = "nabu",
        Turn = new EvalTurn
        {
            Text = "¿cuánto queda del temporizador de la pasta?",
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
                Label = "status",
                Tool = EvalTools.Read,
                Arguments = [Arg.Path("/timers/pasta/status.json")]
            }
        ],
        Permitted = [.. CallPermission.Looking("/timers*")],
        CallCeiling = 4,
        Tier = EvalTier.Smoke,
        // No citation: with the do-it-yourself bullet deleted the model still read the status
        // itself. Delegating a one-call lookup is not a temptation it has.
        Policy = new RunPolicy(2, 3)
    };

    // The task only exists in the conversation: the doctor's notes were said two turns ago and
    // live nowhere a tool can reach, so a worker — which starts with no history — would be handed
    // a summary job with nothing to summarise. Declaring no delegation is how the scenario says
    // the work has to stay where the context is.
    public static Scenario TheSummaryStaysHome => new()
    {
        Name = "work bound to the conversation stays in it",
        AgentId = "jonas",
        History =
        [
            new HistoryExchange(
                "Te cuento lo del médico de esta mañana: la tensión ha salido en catorce nueve, "
                + "así que me ha dicho que baje la sal, que camine cuarenta minutos al día y que "
                + "repita la analítica dentro de tres meses.",
                "Anotado: tensión alta (14/9), menos sal, cuarenta minutos de paseo diario y "
                + "analítica de control en tres meses. ¿Quieres que te lo recuerde más adelante?")
        ],
        Turn = new EvalTurn
        {
            Text = "Resúmemelo en una frase que le pueda mandar a mi hermana.",
            Sender = "fran"
        },
        Instant = EvalInstant.Evening,
        // Nothing to look up and nothing to run: the answer is a sentence over what was already
        // said, and any tool call at all is the model hunting for context it is holding.
        CallCeiling = 1,
        Reply = new ReplyExpectation
        {
            MaxSentences = 2,
            Mentions =
            [
                new SpokenValue("less salt", "sal"),
                new SpokenValue("the follow-up", "tres meses", "3 meses")
            ]
        },
        Claims = [SubAgentPrompt.ContextBoundWorkIsNotDelegated.Id],
        Policy = new RunPolicy(2, 3)
    };

    // Research-shaped on purpose, and no longer requiring the coin to land either side: nine
    // armed runs on 2026-08-19 sent this turn to a worker every time, seven on 2026-08-20 split
    // four delegating to three doing the work in place — same model, same served provider. A
    // scenario requiring either shape fails on the other, so this one asserts what held in every
    // run of both: the answer carries the chronicle's facts, the work is paid for once (one
    // delegation, or one search and the page in two fetches — the ceiling has no room for doing
    // both wholesale), and no reply says who did the work. The delegation-quality claims moved
    // to the exemption backlog beside the coin flip that unseated them.
    public static Scenario TheResearchIsPaidForOnce => new()
    {
        Name = "research is paid for once, in place or delegated",
        AgentId = "jonas",
        Turn = new EvalTurn
        {
            // Summarising the thirty-thousand-character chronicle: heavy enough that either
            // shape is real work, and the turn shape the imperative web scenarios had to be
            // rephrased away from because "Busca la crónica…" kept going to a worker.
            Text = "Busca la crónica de las fiestas del barrio en la web del Cuaderno de barrio "
                   + "y hazme un resumen de lo más destacado para mandárselo a mi madre.",
            Sender = "fran"
        },
        Instant = EvalInstant.Evening,
        // A result that answers everything the parent predictably asks for — including the
        // source url and the dates, because the second armed round showed the parent asking its
        // worker for exactly those, then searching and browsing itself when the answer came
        // back without them. A worker that leaves a gap makes filling it the sensible move,
        // and the scenario then measures the stub.
        WorkerAnswer =
            "La crónica ('Crónica de las fiestas del barrio', en el Cuaderno de barrio, "
            + "http://cuadernodebarrio.local/cronica/fiestas) cubre las fiestas del 1 al 24 de "
            + "agosto: pasacalles de la charanga cada mañana, campeonato de petanca, talleres "
            + "infantiles y verbena hasta la madrugada. Lo más destacado: la calle del Pozo "
            + "repitió premio al balcón mejor engalanado y la rifa solidaria cerró con 1.842 "
            + "euros para el comedor social.",
        MayDelegateTo = ["jonas-worker"],
        Permitted =
        [
            new CallPermission(EvalTools.WebSearch),
            new CallPermission(EvalTools.WebBrowse)
        ],
        // Either honest shape fits with one call of slack; delegating and then re-running the
        // whole research (the redo reflex the trust-the-result rule targets) does not. A partial
        // redo — one stray search after delegating — still fits, which is why the
        // the-result-is-not-redone claim guards here rather than being cited.
        CallCeiling = 4,
        Reply = new ReplyExpectation
        {
            MaxSentences = 5,
            Mentions = [new SpokenValue("the raffle total", "1.842", "1842", "1 842")],
            // Which worker did what is nobody's business, in any of the words the model reaches
            // for when it starts explaining its help.
            NeverSays = ["worker", "subagente", "sub-agente", "agente"]
        },
        Policy = new RunPolicy(2, 3)
    };
}