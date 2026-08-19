using Domain.Prompts;
using Tests.Eval.Fixtures;
using Tests.Eval.Harness;

namespace Tests.Eval.Scenarios;

// The web workflow, against a site this process serves: search, open, read, and only then act.
// The pages are built around what each rule is about — a fact that exists only in the article, a
// page that contradicts its own search snippet, and a form whose confirmation carries a code that
// cannot be guessed. A page outside this site cannot be loaded at all, so a scenario cannot pass
// by leaving the building.
public static class WebScenarios
{
    public static IReadOnlyList<Scenario> All => [TheRestingTime, WhenTheMuseumOpens, BookingAPlace];

    // The answer is in the article and nowhere else: the search snippet describes the recipe
    // without giving the number. A model that answered from the result list has nothing to answer
    // with, and a model that guessed the url never finds the page.
    public static Scenario TheRestingTime => new()
    {
        Name = "an answer comes from the page, not from the result list",
        AgentId = "nabu",
        Turn = new EvalTurn
        {
            Text = "busca cuánto tiene que reposar el gazpacho de Almudena en la nevera",
            Sender = "fran",
            Room = "kitchen",
            SatelliteId = "kitchen-01"
        },
        Instant = EvalInstant.Evening,
        Required =
        [
            new CallExpectation
            {
                Label = "search",
                Tool = EvalTools.WebSearch,
                Arguments = [Arg.Matches("query", "(?i)gazpacho")]
            },
            new CallExpectation
            {
                Label = "open",
                Tool = EvalTools.WebBrowse,
                // By path rather than by url: the host is a loopback address on whichever port was
                // free when the stack came up.
                Arguments = [Arg.Matches("url", "/recetas/gazpacho")]
            }
        ],
        Ordering = [new OrderingConstraint("search", "open")],
        CallCeiling = 4,
        Reply = new ReplyExpectation
        {
            // Spoken, so the source url the written contract asks for must not appear — the check
            // for a url is part of what `Spoken` means.
            Spoken = true,
            MaxSentences = 2,
            Mentions = [new SpokenValue("the resting time", "90", "noventa")]
        },
        // No citation. Searching is forced by the fixture rather than by the prose — a loopback
        // port cannot be guessed — and the two rules about where the answer comes from were both
        // deleted without changing the answer. See ClaimExemptions.

        Policy = new RunPolicy(2, 3),
        // This family's canary: search, open, read, answer is the shape the other two vary.
        Tier = EvalTier.Smoke
    };

    // The snippet is out of date and the page says so in its first line. Answering from the result
    // list is the easy, wrong path, and it is wrong in the way nobody notices — the answer is
    // plausible, fluent and an hour and a half early.
    public static Scenario WhenTheMuseumOpens => new()
    {
        Name = "a page that contradicts its snippet is believed",
        AgentId = "jonas",
        Turn = new EvalTurn
        {
            Text = "Busca a qué hora abre el Museo del Carmen.",
            Sender = "fran"
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
        Permitted = [new CallPermission(EvalTools.WebSearch)],
        CallCeiling = 4,
        Reply = new ReplyExpectation
        {
            // The stale time may well appear beside the new one — "antes a las nueve, ahora a las
            // diez y media" is a good answer. What cannot be missing is the time that is true.
            Mentions = [new SpokenValue("the current opening time", "10:30", "10.30")]
        },
        // No citation: with the read-the-page paragraph and the answer-from-what-you-found bullet
        // both deleted, the reply still carried the page's time rather than the snippet's.
        //
        // Two of four rather than two of three, because this one is genuinely borderline: on the
        // first full run it answered "abre a las 09:00" straight from the stale snippet, twice out
        // of three. Prompt prose saying a snippet is not a source measurably changed nothing, so
        // the marker now rides the search result itself — web_search's envelope and description
        // both say a snippet is a stale summary and the page wins. Move this back to two of three
        // once runs show it holding. See
        // .scratch/findings-from-the-eval/issues/03-an-answer-comes-from-the-snippet.md.
        Policy = new RunPolicy(2, 4)
    };

    // The action half. A ref that was never in a snapshot addresses nothing, so the page has to be
    // seen before it is touched — and the booking code proves the form went through rather than
    // being described: it exists only on the page the submission produces.
    public static Scenario BookingAPlace => new()
    {
        Name = "a form is filled from a snapshot and actually submitted",
        AgentId = "jonas",
        Turn = new EvalTurn
        {
            // Phrased as an instruction to act rather than as a question to look into. The
            // delegation section tells the model to hand web work and multi-step gathering to a
            // worker, and it does exactly that on about half of the runs where the turn reads like
            // research — a finding of its own, recorded with the delegation exemptions, and not
            // what this scenario is about.
            Text = "Abre el formulario de reservas del taller del Cuaderno de barrio y reserva el "
                   + "turno del sábado a las 12:00 a nombre de Fran; dime el código de la reserva.",
            Sender = "fran"
        },
        Instant = EvalInstant.Evening,
        Required =
        [
            new CallExpectation
            {
                Label = "open",
                Tool = EvalTools.WebBrowse,
                Arguments =
                [
                    Arg.Matches("url", "/taller/reserva"),
                    // With the refs, in the one call the contract asks for. Acting on a page whose
                    // structure was never fetched is the failure this scenario is named after, and
                    // a permitted standalone snapshot afterwards does not answer for it.
                    Arg.Flag("snapshot", true)
                ]
            },
            new CallExpectation
            {
                Label = "name",
                Tool = EvalTools.WebAction,
                // A ref that came out of that snapshot, and the name the user gave.
                Arguments = [Arg.Matches("ref", @"^e\d+$"), Arg.Matches("value", "(?i)fran")]
            }
        ],
        // The page has to be open before an element on it can be named.
        Ordering = [new OrderingConstraint("open", "name")],
        Permitted =
        [
            new CallPermission(EvalTools.WebSearch),
            new CallPermission(EvalTools.WebSnapshot),
            new CallPermission(EvalTools.WebAction),
            new CallPermission(EvalTools.WebBrowse)
        ],
        CallCeiling = 9,
        Reply = new ReplyExpectation
        {
            // Only the confirmation page carries it, and only a submission produces that page.
            // The code the Saturday slot books. The Sunday one has a different code, so a reply
            // carrying this one is a reply about the turn the user actually asked for.
            Mentions = [new SpokenValue("the booking code", EvalWeb.SaturdayCode)]
        },
        // Demonstrated red on both runs by deleting the interaction workflow — the snapshot=true
        // paragraph, the one-snapshot-then-chain principle and the re-snapshot recovery row. What
        // the model did instead of snapshotting was hand the whole booking to a worker, which is
        // the same reflex the delegation exemptions record: without the prose it does not attempt
        // the interaction at all.
        Claims = [WebBrowsingPrompt.RefsComeFromASnapshot.Id],
        // The reply of the one scenario that takes many steps is where narrating them tempts.
        Judged =
        [
            new JudgedCheck(WebBrowsingPrompt.StepsAreNotReported.Id,
                "Read the assistant's reply. It must carry the outcome — the booking made and its "
                + "confirmation code — without an account of how it was done. Fail if the reply "
                + "narrates the process: pages opened, forms filled, buttons clicked, snapshots "
                + "taken, tools or workers used. Naming the workshop, the day, the time and the "
                + "code is the outcome and passes; 'I opened the form and typed your name' is a "
                + "step and fails.")
        ],
        Policy = new RunPolicy(2, 4)
    };
}