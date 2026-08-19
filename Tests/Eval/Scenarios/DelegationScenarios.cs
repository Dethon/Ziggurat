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
    public static IReadOnlyList<Scenario> All => [OneLookupIsDoneInPlace, TheSummaryStaysHome];

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
}