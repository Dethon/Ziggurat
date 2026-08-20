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
        WhyNineMinutes, BackFromLisbon, SweepTheNoise
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

    // The turn that gives the model a reason to explain itself: asked where the nine minutes came
    // from, the honest answer names what the user once said — never the plumbing that carried it.
    public static Scenario WhyNineMinutes => new()
    {
        Name = "asked why, the answer names no plumbing",
        AgentId = "nabu",
        History =
        [
            new HistoryExchange(
                "pon un temporizador para la pasta",
                "Nueve minutos de pasta en marcha.")
        ],
        Turn = new EvalTurn
        {
            Text = "¿y por qué nueve minutos?",
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
        // Nothing to look up: the answer is a sentence over what the turn already carries.
        CallCeiling = 1,
        Reply = new ReplyExpectation
        {
            Spoken = true,
            MaxSentences = 2,
            NeverSays =
            [
                "memoria", "memory", "guardad", "almacen", "según mis datos", "contexto",
                "mis registros"
            ]
        },
        Claims = [MemoryPrompts.MechanismIsNeverMentioned.Id],
        Policy = new RunPolicy(2, 3)
    };

    // A fact whose expiry is legible from the turn itself: the trip being prepared is over. The
    // stale project goes without being pointed at, and the fact beside it survives the sweep.
    public static Scenario BackFromLisbon => new()
    {
        Name = "a fact the turn has expired is deleted unasked",
        AgentId = "jonas",
        Turn = new EvalTurn
        {
            Text = "Ya he vuelto de Lisboa, el viaje salió genial.",
            Sender = "fran"
        },
        Instant = EvalInstant.Evening,
        Remembered =
        [
            new RememberedFact("Está preparando un viaje a Lisboa")
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
                Arguments = [Arg.Matches("query", "(?i)lisboa|viaje")]
            }
        ],
        CallCeiling = 2,
        Reply = new ReplyExpectation { NeverSays = ["memoria", "memory", "recuerd"] },
        Claims = [MemoryPrompts.OutdatedFactsAreDeleted.Id],
        Policy = new RunPolicy(2, 3)
    };

    // The bulk-cleanup bullet, on the one store that is actually noisy: four facts that are
    // extraction residue and two worth keeping. Which four go is the judgement under test, and
    // the store afterwards is the only place it shows. The user asked, so naming memory out loud
    // is allowed here; the ceiling leaves room for the forget tool's confirmation round-trip.
    public static Scenario SweepTheNoise => new()
    {
        Name = "a noisy store is swept down to what matters",
        AgentId = "jonas",
        Turn = new EvalTurn
        {
            Text = "Limpia mi memoria: borra los recuerdos que no aporten nada.",
            Sender = "fran"
        },
        Instant = EvalInstant.Evening,
        Remembered =
        [
            new RememberedFact("Cuece la pasta siempre nueve minutos")
            {
                Category = MemoryCategory.Preference,
                Importance = 0.9
            },
            new RememberedFact("Trabaja en Globex como ingeniero de datos") { Importance = 0.9 },
            new RememberedFact("Preguntó por el tiempo el martes pasado")
            {
                Importance = 0.2,
                Forgotten = true
            },
            new RememberedFact("Pidió una pizza cuatro estaciones un viernes")
            {
                Importance = 0.2,
                Forgotten = true
            },
            new RememberedFact("Estaba viendo una serie el domingo por la tarde")
            {
                Importance = 0.2,
                Forgotten = true
            },
            new RememberedFact("Ayer llegó tarde a casa")
            {
                Importance = 0.2,
                Forgotten = true
            }
        ],
        Required =
        [
            new CallExpectation { Label = "sweep", Tool = EvalTools.Forget }
        ],
        CallCeiling = 8,
        Claims = [MemoryPrompts.NoisyStoreIsSwept.Id],
        Policy = new RunPolicy(2, 3)
    };
}