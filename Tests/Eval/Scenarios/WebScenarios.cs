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
    public static IReadOnlyList<Scenario> All =>
    [
        TheRestingTime, WhenTheMuseumOpens, BookingAPlace, TheWholeChronicle,
        TheSiteNamedByName, SigningUpForAstronomy, BackToTheFrontPage, TheThreeStepWizard
    ];

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
        // The reply of the one scenario that takes many steps is where narrating them tempts —
        // and its call log is where reading and structuring blur, so both judgements ride here.
        Judged =
        [
            new JudgedCheck(WebBrowsingPrompt.StepsAreNotReported.Id,
                "Read the assistant's reply. It must carry the outcome — the booking made and its "
                + "confirmation code — without an account of how it was done. Fail if the reply "
                + "narrates the process: pages opened, forms filled, buttons clicked, snapshots "
                + "taken, tools or workers used. Naming the workshop, the day, the time and the "
                + "code is the outcome and passes; 'I opened the form and typed your name' is a "
                + "step and fails."),
            new JudgedCheck(WebBrowsingPrompt.BrowseReadsAndSnapshotStructures.Id,
                "Look at the tool calls. web_browse exists to read a page's content and "
                + "web_snapshot to get its interactive structure; the contract forbids calling "
                + "both for the same purpose on the same page state. A browse with snapshot=true "
                + "that serves both at once passes. Fail if the run browsed a page it only needed "
                + "refs from and then snapshotted the same unchanged page anyway, or took two "
                + "snapshots of the same state with no action in between, or browsed the same url "
                + "twice to re-read content it already had. A re-snapshot after an action that "
                + "changed the page is legitimate recovery and passes.")
        ],
        Policy = new RunPolicy(2, 4)
    };

    // The page is longer than one default browse returns, and the number the user wants is in its
    // closing paragraph. The contract allows exactly one recovery — narrow the extraction or fetch
    // the rest once — and the ceiling is where more than one shows.
    public static Scenario TheWholeChronicle => new()
    {
        Name = "a truncated page is fetched once more and answered from",
        AgentId = "jonas",
        Turn = new EvalTurn
        {
            // An instruction to act rather than a question to look into, the booking scenario's
            // own lesson: phrased as research ("busca...") the whole task went to a worker on the
            // first armed run — and a canned worker browses nothing, so the number never arrived.
            Text = "Abre la crónica de las fiestas del barrio en la web del Cuaderno de barrio y "
                   + "dime cuánto recaudó la rifa solidaria.",
            Sender = "fran"
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
        // One worker tolerated, not required — the reflex the exemptions record reaches this
        // turn shape too, and a canned worker reads no page, so the parent still pays for the
        // tail itself. Search, open, one recovery fetch, the possible worker and one spare: a
        // model that keeps paging past the answer still breaks the ceiling, which is the "once"
        // in fetched-once.
        MayDelegateTo = ["jonas-worker"],
        CallCeiling = 5,
        Reply = new ReplyExpectation
        {
            // Four sentences bounds the written reply against a page that is thirty thousand
            // characters of temptation — the written half of raw-content-is-never-dumped rides
            // here as a side condition.
            MaxSentences = 4,
            Mentions =
            [
                new SpokenValue("the raffle total", "1.842", "1842", "1 842"),
                // The citation the written contract asks for: this reply is written, so the
                // source url has to be in it — the path, because the host is whichever loopback
                // port was free when the stack came up. No leading slash: the mentions matcher
                // wants a non-alphanumeric boundary, and inside a url the character before the
                // path's slash is the port's last digit.
                new SpokenValue("the source url", "cronica/fiestas")
            ]
        },
        Claims =
        [
            WebBrowsingPrompt.PartialContentIsFetchedOnce.Id,
            // The written half; the spoken half is asserted wherever a reply is Spoken, because
            // the no-unspeakables check already forbids a url there.
            WebBrowsingPrompt.UrlsAreCitedOnlyInWriting.Id
        ],
        Policy = new RunPolicy(2, 3)
    };

    // The site named the way a person names it, with nothing to paste. A model that fabricates a
    // domain from the name browses a page nobody serves — and that browse fails here as an
    // unnecessary call, because the only browse this scenario tolerates is the loopback url that
    // exists nowhere but in the search's own results.
    public static Scenario TheSiteNamedByName => new()
    {
        Name = "a named site is searched for, never guessed at",
        AgentId = "jonas",
        Turn = new EvalTurn
        {
            Text = "Mira en la web del Cuaderno de barrio cuánto tiene que reposar el gazpacho "
                   + "de Almudena y dímelo.",
            Sender = "fran"
        },
        Instant = EvalInstant.Evening,
        Required =
        [
            new CallExpectation
            {
                Label = "search",
                Tool = EvalTools.WebSearch,
                Arguments = [Arg.Matches("query", "(?i)gazpacho|cuaderno|barrio")]
            },
            new CallExpectation
            {
                Label = "open",
                Tool = EvalTools.WebBrowse,
                // Anchored to the loopback host on purpose: only a search result can know the
                // port, so a url invented from the site's name — however plausible — matches
                // nothing required or permitted and fails the run.
                Arguments = [Arg.Matches("url", @"^http://127\.0\.0\.1:\d+/recetas/gazpacho")]
            }
        ],
        Ordering = [new OrderingConstraint("search", "open")],
        Permitted = [new CallPermission(EvalTools.WebSearch)],
        // One worker tolerated, not required — the delegation reflex the exemptions record; a
        // canned worker reads no page, so the parent still has to search and open.
        MayDelegateTo = ["jonas-worker"],
        CallCeiling = 5,
        Reply = new ReplyExpectation
        {
            MaxSentences = 3,
            Mentions = [new SpokenValue("the resting time", "90", "noventa")]
        },
        Claims = [WebBrowsingPrompt.UrlComesFromASearch.Id],
        Policy = new RunPolicy(2, 3)
    };

    // Two pages deep and back out, with the return named by the user: the contract says going
    // back is the browser's own back action, never a second browse of the url that was left.
    // The required back call is what a re-browse cannot satisfy.
    public static Scenario BackToTheFrontPage => new()
    {
        Name = "going back is the browser's back, not a second browse",
        AgentId = "jonas",
        Turn = new EvalTurn
        {
            Text = "Abre la portada del Cuaderno de barrio, entra en la receta del gazpacho y "
                   + "dime cuánto reposa; luego vuelve atrás a la portada y dime qué museo "
                   + "aparece en su lista.",
            Sender = "fran"
        },
        Instant = EvalInstant.Evening,
        Required =
        [
            new CallExpectation
            {
                Label = "front page",
                Tool = EvalTools.WebBrowse,
                Arguments = [Arg.Matches("url", @"^http://127\.0\.0\.1:\d+/?$")]
            },
            new CallExpectation
            {
                Label = "back",
                Tool = EvalTools.WebAction,
                Arguments = [Arg.Matches("action", "(?i)back")]
            }
        ],
        Ordering = [new OrderingConstraint("front page", "back")],
        Permitted =
        [
            new CallPermission(EvalTools.WebSearch),
            new CallPermission(EvalTools.WebBrowse),
            new CallPermission(EvalTools.WebSnapshot),
            new CallPermission(EvalTools.WebAction)
        ],
        // One worker tolerated, not required — the delegation reflex the exemptions record; a
        // canned worker browses nothing, so the parent still walks the pages itself.
        MayDelegateTo = ["jonas-worker"],
        CallCeiling = 8,
        Reply = new ReplyExpectation
        {
            MaxSentences = 3,
            Mentions =
            [
                new SpokenValue("the resting time", "90", "noventa"),
                new SpokenValue("the museum on the front page", "Carmen")
            ]
        },
        Claims = [WebBrowsingPrompt.BackIsAnAction.Id],
        Policy = new RunPolicy(2, 3)
    };

    // Three steps, each revealed by the previous click's own diff. The ceiling is the assertion:
    // the flow fits with the one snapshot the first load took, and a model that re-snapshots
    // between every action pays more calls than the ceiling holds.
    public static Scenario TheThreeStepWizard => new()
    {
        Name = "a wizard is walked by chaining refs from each diff",
        AgentId = "jonas",
        Turn = new EvalTurn
        {
            // "En la web del", the chronicle's own lesson: named as a thing rather than a site,
            // a "Cuaderno de barrio" reads like a notebook and the model went hunting for it in
            // the vault.
            Text = "Da de alta a Fran en el padrón de actividades, en la web del Cuaderno de "
                   + "barrio: teléfono 600111222, actividad ajedrez. Dime el resguardo.",
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
                    Arg.Matches("url", "/padron/alta"),
                    Arg.Flag("snapshot", true)
                ]
            }
        ],
        Permitted =
        [
            new CallPermission(EvalTools.WebSearch),
            new CallPermission(EvalTools.WebBrowse),
            new CallPermission(EvalTools.WebSnapshot),
            new CallPermission(EvalTools.WebAction)
        ],
        // Search, open-with-snapshot, three fills, three clicks, one read of the confirmation,
        // the tolerated worker and one spare. Snapshotting between every step does not fit.
        MayDelegateTo = ["jonas-worker"],
        CallCeiling = 11,
        Reply = new ReplyExpectation
        {
            // Only the confirmation prints it, and the confirmation only exists for a form whose
            // three steps were all walked.
            Mentions = [new SpokenValue("the receipt code", EvalWeb.PadronCode)]
        },
        Claims = [WebBrowsingPrompt.ActionsChainFromTheDiff.Id],
        Policy = new RunPolicy(2, 4)
    };

    // The activity field reacts to keystrokes: the suggestions only exist after input events, and
    // the hidden id the form needs is only set by a suggestion's own click handler — so a value
    // merely written into the field, however correct, bounces without a code.
    public static Scenario SigningUpForAstronomy => new()
    {
        Name = "an autocomplete is typed into rather than filled",
        AgentId = "jonas",
        Turn = new EvalTurn
        {
            Text = "Apúntame a la actividad de astronomía en la azotea, a nombre de Fran, y dime "
                   + "el código de inscripción.",
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
                    Arg.Matches("url", "/agenda/apuntarse"),
                    Arg.Flag("snapshot", true)
                ]
            },
            new CallExpectation
            {
                Label = "type",
                Tool = EvalTools.WebAction,
                Arguments =
                [
                    Arg.Matches("action", "(?i)^type$"),
                    Arg.Matches("ref", @"^e\d+$"),
                    Arg.Matches("value", "(?i)astro")
                ]
            }
        ],
        Ordering = [new OrderingConstraint("open", "type")],
        Permitted =
        [
            new CallPermission(EvalTools.WebSearch),
            new CallPermission(EvalTools.WebBrowse),
            new CallPermission(EvalTools.WebSnapshot),
            new CallPermission(EvalTools.WebAction)
        ],
        // One worker is tolerated, not required — the delegation reflex the exemptions record
        // lands here too, and a canned worker cannot produce the code, so the parent still has
        // to do the flow itself. The ceiling absorbs the reflex plus one false start; a model
        // that keeps wandering breaks it.
        MayDelegateTo = ["jonas-worker"],
        CallCeiling = 12,
        Reply = new ReplyExpectation
        {
            // Only the confirmation page carries it, and the confirmation only exists for a form
            // whose activity was picked from the reactive list — a filled value opens nothing,
            // because the suggestions are driven by key events a fill never sends.
            Mentions = [new SpokenValue("the signup code", EvalWeb.SignupCode)]
        },
        Claims = [WebBrowsingPrompt.TypeReactsAndFillSets.Id],
        Policy = new RunPolicy(2, 4)
    };
}