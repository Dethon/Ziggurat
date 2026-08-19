using Domain.Prompts;
using Tests.Eval.Harness;

namespace Tests.Eval.Scenarios;

// Whether to hand the work to somebody else. What is under test is the parent's decision and the
// prompt it wrote, never the worker's answer — the workers are canned, so a scenario here cannot
// fail on what a second model happened to say.
//
// The positive half rides one research-shaped turn: the armed runs that broke the imperative web
// scenarios proved that "Busca…" phrasing sends this model's work to a worker, so that reflex —
// a finding everywhere else — is the fixture here. The two-workers-in-parallel case stays
// withdrawn: see the exemption on `subagents.parallel-parts-are-delegated`.
public static class DelegationScenarios
{
    public static IReadOnlyList<Scenario> All =>
        [OneLookupIsDoneInPlace, TheSummaryStaysHome, TheResearchGoesToAWorker];

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

    // Research-shaped on purpose, where every imperative web scenario learned to avoid it: this
    // is the turn shape the armed runs showed going to a worker. The empty permitted set is the
    // heavy-work assertion — any tool call in the parent's own turn is unnecessary — and the
    // declared delegation polices the prompt: the worker starts with no history, so the site and
    // the museum have to be in the words the parent wrote.
    public static Scenario TheResearchGoesToAWorker => new()
    {
        Name = "research goes to a worker with a self-contained prompt",
        AgentId = "jonas",
        Turn = new EvalTurn
        {
            // The exact shape the armed evidence says delegates: summarising the thirty-
            // thousand-character chronicle is the turn the imperative web scenarios had to be
            // rephrased away from, because "Busca la crónica…" sent the whole task to a worker
            // on both first-run attempts. A lighter ask (one page's opening hours) was tried
            // first and the model rightly did it in place, three runs out of three.
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
        Delegates =
        [
            new DelegationExpectation
            {
                Profile = "jonas-worker",
                Carries = ["Cuaderno de barrio", "crónica"]
            }
        ],
        // The verification is tolerated, not endorsed: nine armed runs showed this model
        // delegating and then re-doing the research itself every time — searching, reading the
        // page, even browsing the url its own worker cited. That distrust is recorded as the
        // finding on heavy-work-is-delegated; what this scenario cites is what the runs do
        // support — the delegation happens, and the prompt it carries is whole.
        Permitted =
        [
            new CallPermission(EvalTools.WebSearch),
            new CallPermission(EvalTools.WebBrowse)
        ],
        // The delegation, and the verification reflex's observed worst: a search, the page in
        // two fetches, and one dead-end browse of the worker's cited url.
        CallCeiling = 7,
        Reply = new ReplyExpectation
        {
            MaxSentences = 5,
            Mentions = [new SpokenValue("the raffle total", "1.842", "1842", "1 842")],
            // Which worker did what is nobody's business, in any of the words the model reaches
            // for when it starts explaining its help.
            NeverSays = ["worker", "subagente", "sub-agente", "agente"]
        },
        Claims =
        [
            SubAgentPrompt.PromptIsSelfContained.Id,
            SubAgentPrompt.NoWorkerIsNamed.Id
        ],
        Judged =
        [
            new JudgedCheck(SubAgentPrompt.SuccessCriteriaAreStated.Id,
                "Read the delegated task prompt (under Delegations). Pass only if it states "
                + "what a good result looks like — what to find (the chronicle of the "
                + "neighbourhood fiestas on the Cuaderno de barrio site) and what to bring back "
                + "(a short summary of the highlights). Fail a prompt that only names the topic "
                + "and leaves the worker to guess what success means."),
            new JudgedCheck(SubAgentPrompt.AnswerIsSynthesised.Id,
                "The worker's answer described the chronicle page and its highlights: fiestas "
                + "from the 1st to the 24th of August, daily brass-band parades, petanque, "
                + "children's workshops, late verbenas, the calle del Pozo winning the balcony "
                + "prize again, and the charity raffle closing at 1.842 euros. Compare that "
                + "with the assistant's reply. Pass a reply that answers the user in its own "
                + "words at the length the request warrants (a short summary to forward). Fail "
                + "a reply that pastes the worker's text verbatim or near-verbatim, or that "
                + "inflates it with process, caveats or filler beyond what was asked.")
        ],
        Policy = new RunPolicy(2, 4)
    };
}