using Domain.Prompts;
using Tests.Eval.Harness;

namespace Tests.Eval.Scenarios;

// Whether to hand the work to somebody else. What is under test is the parent's decision and the
// prompt it wrote, never the worker's answer — the workers are canned, so a scenario here cannot
// fail on what a second model happened to say.
//
// There is no positive when-to-delegate half here, because the eval no longer claims one. It
// was tried and withdrawn twice: against the system-prompt prose (2026-08-18: two parallel
// folders read in place, 17 sequential reads) and against the same rules moved onto the
// subagent tool's own description (2026-08-20: ten probe runs, the parallel turn in place on
// all three, the heavy turn delegating six of seven — a coin either way). The behaviour is
// adequate as it is, so the bullets stay in the prompt uncleaned and unclaimed, and what this
// family asserts is the outcome: work paid for once, in place or delegated, plus the negatives
// the model has actually been caught on.
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
        Guards =
        [
            new Guard(SubAgentPrompt.ASingleCallIsDoneInPlace.Id,
                "Demonstrated on 2026-08-18 with the do-it-yourself bullet deleted: the model still read "
                + "the timer's status itself rather than handing it to a worker. The scenario stays as a "
                + "guard — the same model delegates readily in other shapes, so this is worth watching. "
                + "Watching paid on 2026-08-20: a flake dump showed the invented-station scenario handing "
                + "the play action itself to a worker and voicing the canned 'Hecho' as success — a false "
                + "confirmation the reply checks alone could not catch. The single-action bullet in the "
                + "prose is the answer to that run.")
        ],
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
    // both wholesale), and no reply says who did the work. The delegation-quality claims ride
    // the coin instead of calling it: IfDelegated holds every run that does delegate to the
    // prompt it wrote and the reply it made of the answer, and tallies those runs alone.
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
        IfDelegated =
        [
            new ConditionalDelegation
            {
                Profile = "jonas-worker",
                // The site and the subject: the worker starts with no history, so a prompt
                // saying "la web que me dije" reaches it with the task already lost.
                Carries = ["Cuaderno de barrio", "crónica"],
                Claims =
                [
                    SubAgentPrompt.PromptIsSelfContained.Id,
                    // The reply's NeverSays asserts this on every run; the citation counts only
                    // the runs where a worker existed to be named.
                    SubAgentPrompt.NoWorkerIsNamed.Id
                ],
                Judged =
                [
                    new JudgedCheck(SubAgentPrompt.SuccessCriteriaAreStated.Id,
                        "Read the delegated task prompt (under 'Work handed to workers'). Pass "
                        + "only if it states what a good result looks like — what to find (the "
                        + "chronicle of the neighbourhood fiestas on the Cuaderno de barrio "
                        + "site) and what to bring back (a short summary of the highlights, fit "
                        + "to forward). Fail a prompt that only names the topic and leaves the "
                        + "worker to guess what success means."),
                    // Softened twice, both times against a graded run: reusing the worker's
                    // facts in the worker's order (2026-08-20, pre-conditional), and then
                    // reusing its noun phrases verbatim (2026-08-20, first conditional pass —
                    // the prize's name, the raffle figure and the activity list have one
                    // natural wording, and a faithful two-sentence summary cannot avoid them).
                    // What the claim actually forbids is the answer being the worker's message
                    // passed through whole, or inflated past the ask.
                    new JudgedCheck(SubAgentPrompt.AnswerIsSynthesised.Id,
                        "The worker's answer (quoted under 'Work handed to workers') described "
                        + "the chronicle: the dates, the parades, the petanque, the workshops, "
                        + "the verbena, the balcony prize and the raffle total. Compare it with "
                        + "the assistant's reply. Pass a reply that answers the user at the "
                        + "length the request warrants — a short summary fit to forward. These "
                        + "facts are names, figures and enumerations with one natural wording: "
                        + "the reply reusing those words, even in long runs and in the same "
                        + "order, is a faithful summary and passes. Fail only a reply that "
                        + "reproduces the worker's message essentially whole — the same "
                        + "sentences in the same structure with nothing recast or selected — "
                        + "or that inflates the answer with process, caveats or filler beyond "
                        + "what was asked.")
                ]
            }
        ],
        Permitted =
        [
            new CallPermission(EvalTools.WebSearch),
            new CallPermission(EvalTools.WebBrowse)
        ],
        // Either honest shape fits with one call of slack; delegating and then re-running the
        // whole research (the redo reflex the trust-the-result rule targets) does not.
        CallCeiling = 4,
        Guards =
        [
            new Guard(SubAgentPrompt.TheResultIsNotRedone.Id,
                "Written on 2026-08-20 against an observed redo reflex: nine armed "
                + "runs of the research scenario showed the model delegating and then searching, "
                + "fetching the page twice, and browsing the very url its worker cited. The research "
                + "scenario's ceiling now leaves no room for that wholesale redo, but a partial one — "
                + "a single stray search after delegating — still fits under it, so the ceiling "
                + "guards rather than cites. A citation needs a conditional call-forbiddance (if a "
                + "delegation happened, no web call may follow it) that IfDelegated does not "
                + "express: a delegation is recorded without a sequence, so 'after' is not "
                + "decidable, and forbidding the calls outright on delegating runs would red the "
                + "stray search the ceiling deliberately tolerates.")
        ],
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