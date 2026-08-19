using Domain.Prompts;
using Tests.Eval.Harness;

namespace Tests.Eval.Scenarios;

// Whether to hand the work to somebody else. What is under test is the parent's decision and the
// prompt it wrote, never the worker's answer — the workers are canned, so a scenario here cannot
// fail on what a second model happened to say.
//
// Only the negative half is here. The positive one — two independent parts, two workers — was
// written, run and withdrawn: see the exemption on `subagents.parallel-parts-are-delegated`, which
// is the most interesting thing this family found.
public static class DelegationScenarios
{
    public static IReadOnlyList<Scenario> All =>
        [OneLookupIsDoneInPlace, TheSummaryStaysHome, TheResearchIsHandedOver];

    // What the canned worker answers the research scenario below. A named constant rather than
    // the record's default because two places need the same words: the stack cans the worker
    // with it, and the synthesis rubric hands it to the judge — the judge's material carries the
    // delegated prompt but not the canned reply.
    private const string ResearchWorkerAnswer =
        "Actividades de agosto: ajedrez al aire libre los martes, aquagym los jueves y una "
        + "noche de astronomía el día 28. El polideportivo abre de ocho de la mañana a diez de "
        + "la noche entre semana, y los sábados de nueve a dos.";

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

    // The delegating turn this family waited for — delegated because the user says so, which is
    // the honest shape after 2026-08-19: left to its own judgement the model browsed the url
    // itself, twice, and reported the timeout rather than delegating (the finding on
    // heavy-work-is-delegated). What this scenario witnesses is everything about a delegation
    // once one happens: the url was said two turns ago and lives nowhere a tool can reach, so
    // the delegated prompt has to carry it (the worker starts with no history), and the reply
    // has to answer from what the worker brought back without saying who did what. Four claims
    // ride this one turn — two deterministic, two judged — because they are all statements
    // about the same delegation.
    public static Scenario TheResearchIsHandedOver => new()
    {
        Name = "research is delegated and answered from the worker's result",
        AgentId = "jonas",
        History =
        [
            new HistoryExchange(
                "Apúntate esta web para las cosas del barrio: cuadernodebarrio.example/agenda.",
                "Apuntada: cuadernodebarrio.example/agenda. La miraré cuando me preguntes por "
                + "el barrio.")
        ],
        Turn = new EvalTurn
        {
            Text = "Busca en esa web qué actividades hay este mes y a qué hora abre el "
                   + "polideportivo; delégalo en un trabajador y dime en un par de frases qué "
                   + "encuentra.",
            Sender = "fran"
        },
        Instant = EvalInstant.Evening,
        WorkerAnswer = ResearchWorkerAnswer,
        Delegates =
        [
            new DelegationExpectation
            {
                Profile = "jonas-worker",
                // The url only the parent saw, and the half of the task a lazy prompt drops.
                Carries = ["cuadernodebarrio.example", "polideportivo"]
            }
        ],
        // A second worker for the other half is a legitimate split.
        MayDelegateTo = ["jonas-worker"],
        CallCeiling = 3,
        Reply = new ReplyExpectation
        {
            MaxSentences = 4,
            Mentions =
            [
                // Whole words: the mentions matcher wants a boundary on both sides, so a prefix
                // spelling never matches the word it is a prefix of.
                new SpokenValue("the astronomy night", "astronomía", "astronomia"),
                new SpokenValue("the weekday closing hour", "diez", "22")
            ],
            NeverSays = ["worker", "subagente", "subagent", "jonas-worker"]
        },
        Claims =
        [
            SubAgentPrompt.PromptIsSelfContained.Id,
            SubAgentPrompt.NoWorkerIsNamed.Id
        ],
        Judged =
        [
            new JudgedCheck(SubAgentPrompt.SuccessCriteriaAreStated.Id,
                "Read the delegated prompt the assistant wrote for its worker. Pass if the "
                + "prompt states what a good result looks like — what has to be found (this "
                + "month's activities, the sports centre's opening hours) and what the answer "
                + "should contain or look like when it comes back. Fail if the prompt only "
                + "names a topic or url with no statement of what the worker must return."),
            new JudgedCheck(SubAgentPrompt.AnswerIsSynthesised.Id,
                "The worker answered exactly this: \"" + ResearchWorkerAnswer + "\". Compare "
                + "that with the assistant's final reply. Pass if the reply answers the user "
                + "from that material in its own words, no longer than the question warrants. "
                + "Fail if it pastes the worker's text back verbatim or near-verbatim, or if "
                + "it pads the answer with process, caveats or filler beyond what was asked.")
        ],
        Policy = new RunPolicy(2, 4)
    };
}