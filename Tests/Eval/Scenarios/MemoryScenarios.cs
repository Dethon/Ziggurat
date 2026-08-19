using Domain.DTOs;
using Domain.Prompts;
using Tests.Eval.Harness;

namespace Tests.Eval.Scenarios;

// What a remembered fact is for, and what happens when one stops being true. The facts ride the
// turn as data, the way recall puts them there — so what these scenarios are about is never
// whether the right fact was retrieved, only what the agent did with the one it was handed.
public static class MemoryScenarios
{
    public static IReadOnlyList<Scenario> All =>
    [
        PastaTheWayHeCooksIt, NoLongerAtAcme, ForgetTheFlat,
        WhyNineMinutes, TheTripIsOver, SweepTheNoise
    ];

    // A preference applied without being asked for again, and without being read back. The value
    // is only in the recall block, so the timer's own duration is what says the block was used —
    // a reply that repeated the preference would be the failure the prose exists to prevent.
    public static Scenario PastaTheWayHeCooksIt => new()
    {
        Name = "a remembered preference sets the timer",
        AgentId = "nabu",
        Turn = new EvalTurn
        {
            Text = "pon un temporizador para la pasta",
            Sender = "fran",
            Room = "kitchen",
            SatelliteId = "kitchen-01"
        },
        Instant = EvalInstant.Evening,
        Remembered =
        [
            new RememberedFact("Cuece la pasta siempre nueve minutos")
            {
                Category = MemoryCategory.Preference,
                Importance = 0.9
            },
            new RememberedFact("Le gusta el café solo") { Category = MemoryCategory.Preference }
        ],
        Required =
        [
            new CallExpectation
            {
                Label = "create",
                Tool = EvalTools.Create,
                Arguments =
                [
                    Arg.PathMatches(@"^/timers/[^/]+/timer\.json$"),
                    Arg.Body("content", Arg.Number("durationSeconds", 540))
                ]
            }
        ],
        Permitted = [.. CallPermission.Looking("/timers*")],
        CallCeiling = 4,
        Reply = new ReplyExpectation
        {
            Spoken = true,
            MaxSentences = 1,
            // The mechanism, in the three words a model reaches for when it explains where a
            // number came from. Silence about it is the whole contract here.
            NeverSays = ["memoria", "recuerd", "según lo que sé"]
        },
        // No citation: with the silent-application sentence deleted the timer still came out at
        // nine minutes and the reply still did not read the preference back — see ClaimExemptions.
        Policy = new RunPolicy(2, 3),
        // This family's canary: a fact reaching the model and being used without being spoken is
        // the half of memory that every other scenario here depends on.
        Tier = EvalTier.Smoke
    };

    // A correction with no request to forget in it. The stale fact has to go anyway, and the one
    // beside it has to survive — a forget takes a query and deletes everything the search reached,
    // so "it forgot the right thing" is only half the question.
    public static Scenario NoLongerAtAcme => new()
    {
        Name = "a corrected fact is forgotten unasked",
        AgentId = "jonas",
        Turn = new EvalTurn
        {
            Text = "Ya no trabajo en Acme, ahora estoy en Globex.",
            Sender = "fran"
        },
        Instant = EvalInstant.Evening,
        Remembered =
        [
            new RememberedFact("Trabaja en Acme como ingeniero de datos") { Forgotten = true },
            new RememberedFact("Le gusta el café solo") { Category = MemoryCategory.Preference }
        ],
        Required =
        [
            new CallExpectation
            {
                Label = "forget",
                Tool = EvalTools.Forget,
                Arguments = [Arg.Matches("query", "(?i)acme|trabaj|empleo|empresa")]
            }
        ],
        CallCeiling = 2,
        Reply = new ReplyExpectation
        {
            // Storing the new employer is the extraction pipeline's job and happens out of this
            // turn, so what the reply must not do is announce either half as bookkeeping.
            NeverSays = ["memoria", "memory", "recuerd"]
        },
        // No citation: demonstrated with the correction bullet deleted from the prompt, then from
        // the tool description as well, then with the whole 'When to forget' section gone. The
        // employer memory went every time. Deleting a corrected fact is the model's own default.
        Policy = new RunPolicy(2, 3)
    };

    // The explicit request. Saying the word out loud is allowed here — the user asked — so what is
    // asserted is the deletion, that it took only what was asked, and that the plumbing stays out
    // of the answer.
    public static Scenario ForgetTheFlat => new()
    {
        Name = "a request to forget removes exactly that",
        AgentId = "jonas",
        Turn = new EvalTurn
        {
            Text = "Olvida lo del piso de Chamberí, ya no busco piso.",
            Sender = "fran"
        },
        Instant = EvalInstant.Evening,
        Remembered =
        [
            new RememberedFact("Busca piso de alquiler en Chamberí")
            {
                Category = MemoryCategory.Project,
                Forgotten = true
            },
            new RememberedFact("Le gusta el café solo") { Category = MemoryCategory.Preference }
        ],
        Required =
        [
            new CallExpectation
            {
                Label = "forget",
                Tool = EvalTools.Forget,
                Arguments = [Arg.Matches("query", "(?i)piso|chamber|alquiler")]
            }
        ],
        CallCeiling = 2,
        Reply = new ReplyExpectation { NeverSays = ["memory_forget", "memoria"] },
        // No citation: with the whole 'When to forget' section deleted the flat was still
        // forgotten. What teaches this is the tool's description, which cannot be deleted.
        Policy = new RunPolicy(2, 3)
    };

    // The turn that gives the model a reason to explain itself: asked why, the honest source is
    // the recall block, and the contract says the plumbing stays invisible anyway. The number is
    // explained as something known about the user, never as something looked up in a store.
    public static Scenario WhyNineMinutes => new()
    {
        Name = "an explanation never names the plumbing",
        AgentId = "nabu",
        History =
        [
            new HistoryExchange(
                "pon un temporizador para la pasta",
                "Temporizador de nueve minutos.")
        ],
        Turn = new EvalTurn
        {
            Text = "¿por qué nueve minutos?",
            Sender = "fran",
            Room = "kitchen",
            SatelliteId = "kitchen-01"
        },
        Instant = EvalInstant.Evening,
        Remembered =
        [
            new RememberedFact("Cuece la pasta siempre nueve minutos")
            {
                Category = MemoryCategory.Preference,
                Importance = 0.9
            }
        ],
        // The answer is in the conversation and the recall block; one look at the timers is
        // tolerated, anything more is hunting.
        Permitted = [.. CallPermission.Looking("/timers*")],
        CallCeiling = 1,
        Reply = new ReplyExpectation
        {
            Spoken = true,
            MaxSentences = 2,
            NeverSays =
            [
                "memoria", "memoria contextual", "guardad", "almacen", "base de datos",
                "Memory context", "bloque"
            ]
        },
        Claims = [MemoryPrompts.MechanismIsNeverMentioned.Id],
        Policy = new RunPolicy(2, 3)
    };

    // A fact whose expiry is legible from the turn itself: the trip is over, so the memory about
    // taking it has stopped being true. Nobody asks for anything to be forgotten — the deletion
    // is the agent noticing, and the fact beside it has to survive.
    public static Scenario TheTripIsOver => new()
    {
        Name = "an expired fact is deleted unprompted",
        AgentId = "jonas",
        Turn = new EvalTurn
        {
            Text = "Ya he vuelto del viaje a Berlín, fue genial.",
            Sender = "fran"
        },
        Instant = EvalInstant.Evening,
        Remembered =
        [
            new RememberedFact("Va a viajar a Berlín el 15 de agosto") { Forgotten = true },
            new RememberedFact("Le gusta el café solo") { Category = MemoryCategory.Preference }
        ],
        Required =
        [
            new CallExpectation
            {
                Label = "forget",
                Tool = EvalTools.Forget,
                Arguments = [Arg.Matches("query", "(?i)berl|viaj")]
            }
        ],
        CallCeiling = 2,
        Reply = new ReplyExpectation { NeverSays = ["memoria", "memory"] },
        Claims = [MemoryPrompts.OutdatedFactsAreDeleted.Id],
        Policy = new RunPolicy(2, 3)
    };

    // The bulk cleanup, asked for out loud. Which memories are low-value is the judgement under
    // test: three conversational scraps against three durable facts, all riding the turn with
    // their importances in view. A forget-by-query that reaches several deletes nothing and
    // returns candidates, so the sweep has to finish through memoryIds — the store afterwards is
    // the only assertion that matters.
    public static Scenario SweepTheNoise => new()
    {
        Name = "a noisy store is swept and the real facts survive",
        AgentId = "jonas",
        Turn = new EvalTurn
        {
            Text = "Haz limpieza de mis recuerdos: borra lo que no valga la pena guardar.",
            Sender = "fran"
        },
        Instant = EvalInstant.Evening,
        Remembered =
        [
            new RememberedFact("Trabaja en Globex como ingeniero de datos") { Importance = 0.9 },
            new RememberedFact("Le gusta el café solo")
            {
                Category = MemoryCategory.Preference,
                Importance = 0.8
            },
            new RememberedFact("Su hermana se llama Marta") { Importance = 0.8 },
            new RememberedFact("Preguntó por el tiempo de mañana") { Importance = 0.2, Forgotten = true },
            new RememberedFact("Pidió una pizza cuatro quesos un martes") { Importance = 0.2, Forgotten = true },
            new RememberedFact("Estuvo mirando vuelos baratos un rato") { Importance = 0.3, Forgotten = true }
        ],
        Required =
        [
            new CallExpectation { Label = "sweep", Tool = EvalTools.Forget }
        ],
        // Query, candidates, then ids — or ids straight away, since the recall block carries
        // everything. The ceiling tolerates the two-step flow and one spare.
        CallCeiling = 4,
        Claims = [MemoryPrompts.NoisyStoreIsSwept.Id],
        Policy = new RunPolicy(2, 3)
    };
}