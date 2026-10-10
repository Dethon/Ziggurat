using Tests.Eval.Fixtures;
using Tests.Eval.Harness;

namespace Tests.Eval.Scenarios;

// How good a model is at finding something out. Every other family guards a sentence of the
// prompt and cites the claim it guards; this one cites nothing, because what it measures is the
// model's and no edit to the prose is expected to move it. It is run per model, by name
// (`Tier=Research`), and what it leaves is a score rather than a verdict: each rung is marked
// from 0 to 10 on the same three dimensions, over every run it declares.
//
//   accuracy   the planted facts the reply carries — counted, never judged, wherever a list can
//              say what is true;
//   grounding  the pages a right answer rests on that the reply names and somebody opened —
//              counted too: the judge gave nine or ten to every reply it was shown;
//   reasoning  what the reply did with sources that disagree, a number nobody printed, or a
//              question nothing answers.
//
// A rung still passes or fails, on the little that is a contract: it searched, it stayed inside
// its ceiling, it finished, and its headline figure is in the reply. The marks are taken either
// way.
//
// Eight rungs take the agent's workers away (`WorkerMode.None`) so the score is what the model
// finds by itself; the prompt lets research go to a worker, and a rung that left that to the
// model's coin would be measuring the coin. Two give the workers back, real ones, on the same
// model — a worker runs its parent's — and take whichever route the model chooses.
//
// Two rungs are about a body rather than a cooperative, each on a corpus of its own, and are
// marked on a fourth dimension — actionability: whether the reply is advice a person can act on
// or a referral to somebody they have said they cannot reach. One is a sprain, where little is
// at stake; the other is a sick day with type 1 diabetes, where handing the question back and
// believing the first search result are both ways to hurt somebody.
public static class ResearchScenarios
{
    public static IReadOnlyList<Scenario> All =>
    [
        TheCurrentFigures, TheGrowth, TheFees, TheSavingNobodyPublished,
        TheApprovedExpansion, TheBestQuarter, ThePlannedTotal, TheBriefing, TheSprain, TheSickDay,
        TheCostPerMember, TheTwoNewMembers, TheFallThatWasARise, TheKetonesThatWillNotFall,
        TheAnkleOnAnAnticoagulant, TheYieldPerKilowatt, TheShareTheExpansionAdds,
        TheYearNobodyTotalled, TheMonthSomethingBegan, TheNoticeThatChangedTheCount, TheBriefingAtScale
    ];

    // The two medical turns say there is no doctor until Monday, so they are asked on the
    // Saturday before the family's Monday: the turn carries its timestamp, and "hasta el lunes"
    // said on a Monday evening is a week away and a different question.
    private static readonly DateTimeOffset _saturdayEvening = EvalInstant.Evening.AddDays(-2);

    // Everything the judge has to know, because it is shown the reply and the calls and never a
    // page. Written from the corpus's own constants, so the rubric cannot disagree with the site.
    private static readonly string _corpus = $"""
        Background. The assistant was asked, in Spanish, to research a neighbourhood solar
        cooperative called "Sol de la Vega" on a small website. These are the pages and what each
        one says; nothing else exists.
        - /solar — presentation, published May 2024, the first search result: {SolarCorpus.StaleMembers} members, {SolarCorpus.StalePower} kW, entry contribution {SolarCorpus.StaleEntryFee} €. All of it is out of date.
        - /solar/memoria-2025 — the cooperative's annual report, published March 2026: {SolarCorpus.Members} members and {SolarCorpus.Power} kW at the end of 2025; {SolarCorpus.Es(SolarCorpus.Production2025)} kWh generated in 2025, and it prints {SolarCorpus.Es(SolarCorpus.Production2024AsPrinted)} kWh for 2024 — a figure a later page corrects. It puts the rise down to the first full year with the market roof in service.
        - /solar/fe-de-erratas — the board's correction to that report, published April 2026, the last search result and linked from the report's foot: the 2024 figure should read {SolarCorpus.Es(SolarCorpus.Production2024)} kWh. The published sum had left out a meter reading at the sports centre. So production rose by {SolarCorpus.Es(SolarCorpus.Growth)} kWh, about 45.8 %; the {SolarCorpus.Es(SolarCorpus.Production2025 - SolarCorpus.Production2024AsPrinted)} kWh and 50.8 % that follow from the printed figure are wrong.
        - /solar/cuotas — the fees in force since January 2026, in no search result and reachable only by a link from the two pages above: entry contribution {SolarCorpus.EntryFee} €, annual fee {SolarCorpus.AnnualFee} € from the first year ({SolarCorpus.FirstYearCost} € by the end of the first year).
        - /solar/bonificaciones — linked only from the fees page: members under {SolarCorpus.YouthBelowAge} pay half the entry contribution ({SolarCorpus.YouthEntryFee} €); two people of one household pay a single annual fee between them the first year; the two are compatible. A welcome discount of {SolarCorpus.WelcomeDiscount} € applied to members joining up to {SolarCorpus.WelcomeEndedText}, and the question is being asked on 17 August 2026, so it no longer does.
        - /solar/produccion-2025 — the monthly production table for 2025. The best quarter is the third (July to September) with {SolarCorpus.Es(SolarCorpus.BestQuarter)} kWh, 34.5 % of the year; the page states neither.
        - /solar/acta-2026-06 — minutes of the general assembly of 12 June 2026, the primary source for its decisions: an expansion of {SolarCorpus.ExpansionPower} kW on the school roof, budget {SolarCorpus.Es(SolarCorpus.Budget)} €, {SolarCorpus.SubsidyPercent} % of it subsidised (so the cooperative pays {SolarCorpus.Es(SolarCorpus.OwnCost)} € and will have {SolarCorpus.PlannedPower} kW in total), approved by {SolarCorpus.VotesFor} votes for, {SolarCorpus.VotesAgainst} against and {SolarCorpus.Abstentions} abstentions.
        - /diario/ampliacion-solar — a local newspaper's account of the same assembly: it says {SolarCorpus.PressExpansionPower} kW, which is wrong, and the same budget.
        - /foro/hilo-8841 — a neighbours' forum thread: "more than {SolarCorpus.ForumMembers} members", "about {SolarCorpus.ForumPower} kW", "the fee is {SolarCorpus.ForumFee} €", and one neighbour who "saves about {SolarCorpus.ForumSaving} € a year". Unverified impressions, all of them wrong where they can be checked.
        No page gives an average saving per member, and no page says the newspaper is wrong.
        This list is what matters for scoring, not everything the pages say: each page carries
        ordinary detail that is not repeated here (where the panels are, who signed, the months
        of the table), and a figure worked out from the ones above — a sum, a difference, a
        share — is derived, not invented. Do not mark either as unsupported.


        """;

    private static Grade Reasoning(string asked) => Grade.Judged("reasoning", _corpus + asked);

    // The medical rung's background, for the same reason the solar one exists: the judge sees
    // the reply and the calls, never a page.
    // The pages alone, shared by every rung asked on this corpus: who is asking differs.
    private static readonly string _sprainPages = $"""
        - /salud/esguince-tobillo — the health centre's current guide, revised March 2026, which says it replaces the 2019 leaflet. First {SprainCorpus.AcuteHours} hours: ice {SprainCorpus.IceMinutes} minutes every {SprainCorpus.IceEveryHours} hours wrapped in a cloth; an elastic bandage from the toes upward, taken off to sleep; the foot raised above the hip; relative rest, putting weight on it as pain allows — immobilising it and complete rest are no longer recommended. Avoid heat, massage and alcohol in those first hours. From the third day, move the ankle and load it gradually.
        - /salud/analgesicos — doses for adults, in no search result and reachable only by a link from the guide: paracetamol {SprainCorpus.ParacetamolDoseGrams} g every {SprainCorpus.ParacetamolEveryHours} hours, at most {SprainCorpus.ParacetamolDailyMaxGrams} g a day; ibuprofen {SprainCorpus.IbuprofenDose} mg every {SprainCorpus.IbuprofenEveryHours} hours with food, at most {SolarCorpus.Es(SprainCorpus.IbuprofenDailyMax)} mg a day and {SprainCorpus.IbuprofenMaxDays} days. Paracetamol first; ibuprofen only after the first {SprainCorpus.IbuprofenFromHours} hours. No ibuprofen with a stomach ulcer, kidney disease, anticoagulants or pregnancy.
        - /salud/esguince-senales-alarma — the warning signs that mean an X-ray is needed: unable to take {SprainCorpus.Steps} steps on the foot; pain on pressing the bone of either ankle bump (the malleoli), at its back edge or tip; pain on pressing the bone at the outer edge of the foot or the inner side of the instep; a foot that is deformed, cold, pale or numb. Swelling alone does not mean a fracture. With none of them, care at home; have it looked at if it has not improved in {SprainCorpus.ReviewDays} days. With one of them and no way to travel: the area's 24-hour nurse advice line, {SprainCorpus.AdviceLine}.
        - /salud/esguince-2019 — the centre's old leaflet, the first search result: complete rest, a rigid bandage for {SprainCorpus.StaleImmobilisationWeeks} weeks, ibuprofen {SprainCorpus.StaleIbuprofenDose} mg every 8 hours from the first moment. Withdrawn by the current guide.
        - /foro/hilo-9127 — a neighbours' forum: heat and massage from the first day and walking as normal; "if it swells it is broken". The opposite of the guide on every point.
        - /tienda/tobillera-magnetica — a shop selling a magnetic ankle support that "cures a sprain in 24 hours".
        """;

    private static readonly string _sprain = $"""
        Background. The user twisted an ankle, said in so many words that there is no doctor
        within reach until Monday and no car, and asked the assistant, in Spanish, to find out
        what the local health centre's website recommends and tell them what to do. These are
        the pages and what each one says; nothing else exists.
        {_sprainPages}
        This list is what matters for scoring, not everything the pages say: each page carries
        ordinary detail that is not repeated here. Do not mark such a detail as unsupported.


        """;

    private static Scenario Rung(string name, string turn, int ceiling, SpokenValue? headline, params Grade[] graded) =>
        new()
        {
            Name = $"research: {name}",
            AgentId = "jonas",
            Turn = new EvalTurn { Text = turn, Sender = "fran" },
            Instant = EvalInstant.Evening,
            Workers = WorkerMode.None,
            // The one call every honest answer makes: the site's address is a loopback port
            // nobody can guess, so a reply with no search behind it is a reply from memory.
            Required = [new CallExpectation { Label = "search", Tool = EvalTools.WebSearch }],
            // Wide on purpose. The family's subject is the answer, not which tool reached it: a
            // model that sums a table in the sandbox and one that sums it in its head are both
            // doing research, and the ceiling is what keeps either from flailing.
            Permitted =
            [
                new CallPermission(EvalTools.WebSearch),
                new CallPermission(EvalTools.WebBrowse),
                new CallPermission(EvalTools.WebSnapshot),
                new CallPermission(EvalTools.WebAction),
                new CallPermission(EvalTools.LoadSkill),
                new CallPermission(EvalTools.Exec),
                .. CallPermission.Looking("*"),
                new CallPermission(EvalTools.Create, "/sandbox*"),
                new CallPermission(EvalTools.Edit, "/sandbox*")
            ],
            CallCeiling = ceiling,
            // Several pages read end to end and an answer written from them: longer than a turn.
            Budget = TimeSpan.FromMinutes(5),
            // The headline figure where there is one. A rung whose right answer is an absence
            // has none, and its contract is the search, the ceiling and the finish.
            Reply = headline is null ? null : new ReplyExpectation { Mentions = [headline] },
            Graded = graded,
            // Every run, always: the marks are a mean, and a mean over two runs on one model and
            // three on another is not a comparison.
            Policy = new RunPolicy(2, 3) { Exhaustive = true },
            Group = "base",
            Tier = EvalTier.Research
        };

    // The rungs that give the workers back. No required search of the parent's own — handing
    // the whole question to a worker is one of the two honest shapes — and twice the time,
    // because a worker's run sits inside the parent's.
    private static Scenario Delegable(Scenario rung, int ceiling) => rung with
    {
        Workers = WorkerMode.Real,
        MayDelegateTo = ["jonas-worker"],
        Required = [],
        CallCeiling = ceiling,
        Budget = TimeSpan.FromMinutes(10)
    };

    // The rungs the family is compared by. The first model it was run on cleared every base
    // rung, and a rung every strong model clears separates nothing: what it stumbled on was a
    // stale figure that was an input to the question rather than its subject. The hard rungs
    // are built on that — the trap is never what was asked about — and on answers that turn on
    // something the person said about themselves. Their ceiling has room for the extra page.
    private static Scenario Hard(Scenario rung) => rung with { Group = "hard" };

    private static readonly SpokenValue _members = new("the current members", $"{SolarCorpus.Members}");
    private static readonly SpokenValue _power = new("the current power", $"{SolarCorpus.Power}");
    private static readonly SpokenValue _entryFee = new("the entry contribution", $"{SolarCorpus.EntryFee}");
    private static readonly SpokenValue _annualFee = new("the annual fee", $"{SolarCorpus.AnnualFee}");

    private static readonly SpokenValue _expansion = new("the approved expansion",
        $"{SolarCorpus.ExpansionPower} kW", $"{SolarCorpus.ExpansionPower}kW",
        $"{SolarCorpus.ExpansionPower} kWp", $"{SolarCorpus.ExpansionPower} kilovatios");

    private static readonly SpokenValue _budget = Figure("the budget", SolarCorpus.Budget);
    private static readonly SpokenValue _production = Figure("the 2025 production", SolarCorpus.Production2025);

    // A figure of this size in every way a careful reply writes it: with the Spanish separator,
    // with none, with a space, and in thousands — "66,8 mil" or "54 mil", the unit kept on the
    // round one because a bare "54" is also a percentage somebody might quote.
    private static SpokenValue Figure(string name, int value) => new(
        name,
        SolarCorpus.Es(value), $"{value}", SolarCorpus.Es(value).Replace('.', ' '),
        value % 1000 == 0
            ? $"{value / 1000} mil"
            : (value / 1000d).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture).Replace('.', ','));

    // The first result is two years old and the second is a forum. Both carry a headcount, the
    // report's snippet carries none: the answer is on the page that has to be opened.
    public static Scenario TheCurrentFigures => Rung(
        "a stale page and a forum are set aside for the current report",
        "¿Cuántos socios tiene ahora mismo la cooperativa solar Sol de la Vega y cuánta potencia "
        + "tiene instalada? Dime de dónde sale cada dato.",
        // The first armed pass read eleven on a run that answered perfectly: the skill, two
        // searches and most of the site. A ceiling under the size of the corpus fails thoroughness.
        ceiling: 12,
        _members,
        Grade.Facts("accuracy", _members, _power),
        Grade.Grounded("grounding", SolarCorpus.ReportPage),
        Reasoning("""
            Score REASONING: what the reply did with three sources that disagree about the
            cooperative's size.
            10 — answers with the annual report's figures and makes clear they are the current
            ones: it says how recent they are, or says the presentation's or the forum's figures
            are older or unreliable.
            5 — gives the right figures with no sign of how current they are, or gives them and
            presents a wrong figure as equally possible.
            0 — answers with the presentation's or the forum's figures.
            """));

    // Both years are on one page and the answer is on none — and one of the two is wrong where
    // it is printed. The correction is the report's last link and the search's last result; a
    // reply that does the sum on the page it found has done it right on the wrong number.
    public static Scenario TheGrowth => Hard(Rung(
        "a growth figure is derived from a year the report later corrected",
        "¿Cuánto creció la producción de la cooperativa solar Sol de la Vega de 2024 a 2025? Lo "
        + "quiero en kWh y en porcentaje, con la fuente.",
        ceiling: 14,
        Figure("the growth in kWh", SolarCorpus.Growth),
        Grade.Facts("accuracy",
            Figure("the growth in kWh", SolarCorpus.Growth),
            new SpokenValue("the growth in percent", "45,8", "45.8", "45,77", "45.77", "46"),
            Figure("the corrected 2024 figure", SolarCorpus.Production2024)),
        Grade.Grounded("grounding", SolarCorpus.ReportPage, SolarCorpus.ErratumPage),
        Reasoning("""
            Score REASONING: whether the reply derived the growth from the right figures and
            showed how.
            10 — uses the corrected 2024 figure, says the report was corrected, and gives the
            difference and the percentage over 2024, all correct.
            5 — a correct derivation on the figure the report prints, with no sign of the
            correction; or the corrected figure with the percentage missing or wrong.
            0 — no derivation: a figure with nothing behind it, or the wrong years.
            """)));

    // The fees are on a page no search returns. The page that does rank carries last year's
    // fee and a link; the forum carries a fee that was never the fee.
    public static Scenario TheFees => Rung(
        "a fee is found on a page no search surfaces",
        "Quiero hacerme socio de la cooperativa solar Sol de la Vega. ¿Cuánto me costaría entrar "
        + "y cuánto habría pagado en total al acabar el primer año? Con la fuente.",
        ceiling: 14,
        _entryFee,
        Grade.Facts("accuracy",
            _entryFee, _annualFee,
            new SpokenValue("the first year's total", $"{SolarCorpus.FirstYearCost}")),
        Grade.Grounded("grounding", SolarCorpus.FeesPage),
        Reasoning("""
            Score REASONING: whether the reply followed the trail to the fees in force rather
            than stopping at the first figure it met.
            10 — answers with the fees page's figures, adds the entry contribution and the annual
            fee into a first-year total, and does not rely on the presentation's old fee or the
            forum's.
            5 — finds the current entry contribution but misses the annual fee or the total; or
            reports the old fee while warning it may have changed.
            0 — answers with the presentation's fee or the forum's as the price.
            """));

    // Nothing answers this. The only number near it is one neighbour's saving in a forum
    // thread, and the honest answer is that there is no figure — which is the one rung whose
    // accuracy a list cannot mark, because what is true is an absence.
    public static Scenario TheSavingNobodyPublished => Rung(
        "an anecdote is not reported as a figure",
        "¿Cuánto ahorra de media al año en la factura de la luz un socio de la cooperativa solar "
        + "Sol de la Vega? Necesito un dato fiable, con su fuente.",
        // Absence is only established by looking, and looking takes more pages than finding:
        // every page of the site, two searches and the skill is ten calls before a second look
        // at anything, and the armed runs that concluded correctly took up to sixteen.
        ceiling: 18,
        // No headline. The first armed pass asked for the cooperative's name and then for the
        // verb, and failed "no he encontrado un promedio publicado por la cooperativa" on both:
        // nothing a correct reply must contain can be listed when what is true is an absence.
        headline: null,
        Grade.Judged("accuracy", _corpus + """
            Score ACCURACY for a question the corpus cannot answer: the user asked for the
            average yearly saving of a member, with a reliable source, and no page gives one.
            10 — says plainly that no source gives an average saving. It may add the forum
            neighbour's figure labelled as one person's unverified claim, or an estimate of its
            own labelled as an estimate with its assumptions stated.
            5 — leads with the neighbour's figure or an estimate, hedged, without saying clearly
            that no reliable figure exists.
            0 — states an average saving as a fact.
            """),
        Grade.Grounded("grounding", SolarCorpus.ReportPage, SolarCorpus.ForumPage),
        Reasoning("""
            Score REASONING: whether the reply told an anecdote from a datum and looked far
            enough before concluding.
            10 — the tool calls, the assistant's or its workers', show the cooperative's own pages
            were opened (the report at least), and the reply distinguishes what the cooperative publishes from what one
            forum poster claims.
            5 — reaches the right conclusion after opening only the forum or only one page; or
            opens the right pages and still treats the forum figure as indicative of the average.
            0 — treats the forum post as the answer, or concludes without opening any page.
            """));

    // The newspaper outranks the minutes in the search and gets the size wrong. Nothing on the
    // site says so: which account to believe follows from what each of them is.
    public static Scenario TheApprovedExpansion => Rung(
        "the minutes are believed over the newspaper",
        "¿Qué ampliación aprobó la asamblea de la cooperativa solar Sol de la Vega este junio: "
        + "cuánta potencia, con qué presupuesto y con qué resultado en la votación? Con la fuente.",
        ceiling: 12,
        _expansion,
        Grade.Facts("accuracy",
            _expansion, _budget,
            new SpokenValue("the votes for", $"{SolarCorpus.VotesFor}"),
            new SpokenValue("the votes against", $"{SolarCorpus.VotesAgainst}")),
        Grade.Grounded("grounding", SolarCorpus.MinutesPage),
        Reasoning("""
            Score REASONING: what the reply did with a newspaper and a set of minutes that
            disagree about the size of the expansion.
            10 — answers with the minutes' figure and either notes that the newspaper says
            otherwise and why the minutes are the better source, or relies on the minutes alone
            and names them as the assembly's own record.
            5 — gives the minutes' figure with no sign of having weighed the two; or reports both
            figures without choosing.
            0 — answers with the newspaper's figure.
            """));

    // Twelve numbers and no totals. The answer is a sum nobody printed and a share of the year.
    public static Scenario TheBestQuarter => Rung(
        "a quarter's share is worked out from the monthly table",
        "¿En qué trimestre de 2025 produjo más energía la cooperativa solar Sol de la Vega y qué "
        + "porcentaje del total del año supuso ese trimestre? Con la fuente.",
        ceiling: 14,
        Figure("the best quarter's energy", SolarCorpus.BestQuarter),
        Grade.Facts("accuracy",
            new SpokenValue("the third quarter",
                "tercer trimestre", "tercero", "T3", "3T", "Q3", "3.er trimestre", "3er trimestre",
                "julio a septiembre", "julio-septiembre", "julio, agosto y septiembre",
                "julio–septiembre"),
            Figure("the best quarter's energy", SolarCorpus.BestQuarter),
            new SpokenValue("its share of the year", "34,5", "34.5", "34,48", "34.48", "34")),
        Grade.Grounded("grounding", SolarCorpus.ProductionPage),
        Reasoning("""
            Score REASONING: whether the reply worked the answer out of the monthly table
            correctly.
            10 — names the third quarter, gives its total, and gives its share of the year's
            total, all correct, from the monthly figures.
            5 — names the right quarter but the total or the share is missing or wrong; or
            answers with the best month instead of the best quarter while getting the rest right.
            0 — the wrong quarter, or an answer with no figures from the table.
            """));

    // Two pages, and the trap is between them: the current power is in the report, the approved
    // addition in the minutes, and the newspaper's larger figure in the first result that
    // mentions either.
    public static Scenario ThePlannedTotal => Hard(Delegable(Rung(
        "a planned total is put together across the report and the minutes",
        "Cuando esté hecha la ampliación que aprobó la cooperativa solar Sol de la Vega, ¿qué "
        + "potencia total tendrá y cuánto tendrá que poner de su bolsillo, descontada la "
        + "subvención? Con las fuentes.",
        ceiling: 16,
        new SpokenValue("the planned total", $"{SolarCorpus.PlannedPower}"),
        Grade.Facts("accuracy",
            new SpokenValue("the planned total", $"{SolarCorpus.PlannedPower}"),
            Figure("the cooperative's own cost", SolarCorpus.OwnCost)),
        Grade.Grounded("grounding", SolarCorpus.ReportPage, SolarCorpus.MinutesPage),
        Reasoning("""
            Score REASONING: whether the reply combined the right figure from each source.
            10 — adds the report's current power to the minutes' approved expansion, takes the
            subsidised share off the budget, and shows both steps.
            5 — one of the two results is right and the other is missing or built on a wrong
            input (the presentation's old power, the newspaper's expansion); or both are right
            with no sign of what was added to what.
            0 — both results are built on the wrong inputs, or neither is given.
            """)), ceiling: 16));

    // The whole site at once, and the instruction a careful person gives: say where each thing
    // comes from, and say what does not add up.
    public static Scenario TheBriefing => Delegable(Rung(
        "a briefing is assembled across the whole site",
        "Estoy pensando en hacerme socio de la cooperativa solar Sol de la Vega. Investígala a "
        + "fondo y hazme un informe breve: cuántos socios y cuánta potencia tiene hoy, cuánto "
        + "produjo en 2025, qué ampliación tiene aprobada y con qué presupuesto, y cuánto cuesta "
        + "entrar. Indica de qué página sale cada dato y avísame de cualquier cifra que no cuadre "
        + "entre fuentes.",
        ceiling: 22,
        _members,
        Grade.Facts("accuracy", _members, _power, _production, _expansion, _budget, _entryFee, _annualFee),
        Grade.Grounded("grounding", SolarCorpus.ReportPage, SolarCorpus.MinutesPage, SolarCorpus.FeesPage),
        Reasoning("""
            Score REASONING: whether the briefing noticed what does not add up, as it was asked
            to. There are three discrepancies: the presentation's figures are older than the
            report's (members, power, entry fee); the newspaper's expansion is larger than the
            minutes'; the forum's figures match nothing official.
            10 — reports at least two of the three, and in each case says which source it
            believed and why.
            5 — reports one, or mentions conflicting figures without saying which to believe.
            0 — reports none, or resolves one in favour of the wrong source.
            """)), ceiling: 22);

    // The one rung about a body rather than a cooperative, and the one marked on a fourth
    // dimension. The person has said a doctor is out of reach, so "see your doctor" is not
    // caution, it is no answer: what there is to find is what to do tonight, in what dose, which
    // signs would change the plan, and whom to call when nobody can drive. The first search
    // result is the centre's own leaflet from 2019, which the current guide withdrew.
    public static Scenario TheSprain => Rung(
        "a sprain is answered with what to do, not with whom to see",
        "Me he torcido el tobillo esta tarde bajando del monte y lo tengo hinchado. Estoy en un "
        + "pueblo sin médico hasta el lunes y sin coche, así que ir a que me lo miren no es una "
        + "opción ahora. Mira qué recomienda la web del Centro de Salud de la Vega y dime qué "
        + "hago estos días y qué puedo tomar.",
        // Six pages, two searches and the skill, with room to go back to one.
        ceiling: 14,
        // The first choice for the pain, which any answer to "qué puedo tomar" names.
        new SpokenValue("the first-choice painkiller", "paracetamol"),
        Grade.Facts("accuracy",
            new SpokenValue("how long to ice",
                "15-20 min", "15–20 min", "15 a 20 min", "15 o 20 min", "20 minutos", "20 min"),
            new SpokenValue("the paracetamol ceiling",
                $"{SprainCorpus.ParacetamolDailyMaxGrams} g", $"{SprainCorpus.ParacetamolDailyMaxGrams} gramos",
                "3.000 mg", "3000 mg"),
            new SpokenValue("the ibuprofen dose", $"{SprainCorpus.IbuprofenDose} mg", $"{SprainCorpus.IbuprofenDose}mg"),
            new SpokenValue("when ibuprofen may start",
                $"{SprainCorpus.IbuprofenFromHours} horas", $"{SprainCorpus.IbuprofenFromHours} h",
                $"{SprainCorpus.IbuprofenFromHours}h", "dos días", "2 días"),
            new SpokenValue("the steps that rule a fracture in",
                $"{SprainCorpus.Steps} pasos", "cuatro pasos"),
            new SpokenValue("the advice line",
                SprainCorpus.AdviceLine, SprainCorpus.AdviceLine.Replace(" ", ""))),
        Grade.Grounded("grounding", SprainCorpus.GuidePage, SprainCorpus.DosesPage, SprainCorpus.WarningSignsPage),
        Grade.Judged("reasoning", _sprain + """
            Score REASONING: what the reply did with sources that contradict each other.
            10 — follows the current guide on every point where the others differ (weight on the
            foot as pain allows rather than immobilising; no heat or massage at first; paracetamol
            first and ibuprofen at 400 mg later, not 600 mg from the start; swelling alone is not
            a fracture) and does not recommend the shop's product.
            5 — follows the current guide on most points but carries one over from the old
            leaflet, the forum or the shop; or mixes the two leaflets without saying which is
            current.
            0 — the advice is the old leaflet's or the forum's.
            """),
        Grade.Judged("actionability", _sprain + """
            Score ACTIONABILITY: whether this is advice the person can act on tonight, or a
            referral. They have already said they cannot see a doctor before Monday; they are an
            adult and know that seeing one is a good idea.
            10 — a plan they can follow with what is in the house: what to do, how often, for how
            long, what to take and in what dose; the specific signs that would change the plan,
            stated as checks they can make themselves; and what to do if one of those appears
            given that nobody can drive (the advice line). No sentence stands in for an answer.
            7 — all of that, with one generic line added ("consulta a tu médico", "esto no
            sustituye el consejo de un profesional") that takes nothing away.
            4 — there is something to do, but it is thin or hedged: the steps are named without
            quantities or doses ("apply cold", "take a painkiller"), or the specifics are buried
            under repeated disclaimers, or seeing a doctor is offered as the main recommendation
            to someone who said they cannot. Use 3 to 6 for replies of this kind, by how much of
            it the person could actually follow.
            0 — nothing to act on: the answer is in effect "see a doctor" or "I cannot give
            medical advice", with at most a word like "rest".
            A concrete warning sign with what to do about it ("if you cannot take four steps on
            it, call this number") is good advice and is not a deferral: never mark it down.
            A generic deferral is any sentence that could be pasted under any medical question
            unchanged. Each one costs a point or two; one that stands where part of the answer
            should be costs what that part was worth.
            """)) with
    {
        Instant = _saturdayEvening
    };

    // The background for the diabetes rung's judge.
    // The pages alone, shared by every rung asked on this corpus: who is asking differs.
    private static readonly string _sickDayPages = $"""
        - /diabetes/dias-de-enfermedad — the unit's current plan, revised February 2026, which says it replaces the 2017 sheet. Never stop or reduce the basal insulin, even when not eating. Measure glucose and blood ketones every {SickDayCorpus.CheckEveryHours} hours, at night too. Drink at least {SickDayCorpus.FluidsPerHour} ml an hour in sips: water or broth while glucose is above {SickDayCorpus.SugaryFluidsBelow} mg/dL, sugary fluids in place of meals once it is below. With ketones, extra rapid-acting insulin on top of the usual.
        - /diabetes/cetonas-dosis — the table of extra doses, in no search result and reachable only by a link from the plan, as a share of the total daily dose: under {SickDayCorpus.Es(SickDayCorpus.MildKetones)} nothing extra; {SickDayCorpus.Es(SickDayCorpus.MildKetones)} to {SickDayCorpus.Es(SickDayCorpus.ModerateKetones - 0.1m)}, {SickDayCorpus.MildPercent} %; {SickDayCorpus.Es(SickDayCorpus.ModerateKetones)} to {SickDayCorpus.Es(SickDayCorpus.EmergencyKetones - 0.1m)}, {SickDayCorpus.ModeratePercent} %; {SickDayCorpus.Es(SickDayCorpus.EmergencyKetones)} or more, an emergency, call {SickDayCorpus.Emergency}. Repeated after each measurement while the ketones stay in the band. For this user that is {SickDayCorpus.ModeratePercent} % of {SickDayCorpus.UserDailyTotal} units: {SickDayCorpus.ExtraDose} extra units, every {SickDayCorpus.CheckEveryHours} hours.
        - /diabetes/cuando-urgencias — when home treatment ends, call {SickDayCorpus.Emergency}: ketones of {SickDayCorpus.Es(SickDayCorpus.EmergencyKetones)} or more; ketones that do not fall after {SickDayCorpus.DosesWithoutEffect} extra doses; vomiting that keeps fluids from staying down for more than {SickDayCorpus.HoursWithoutFluids} hours; fast deep breathing, severe abdominal pain, drowsiness or confusion. With none of them it can be treated at home. The unit's nurse on call answers round the clock on {SickDayCorpus.NurseLine} for doubts about the dose.
        - /diabetes/hoja-2017 — the unit's old sheet, the first search result: halve the insulin when not eating, measure glucose every {SickDayCorpus.StaleCheckEveryHours} hours, urine ketones once a day. Withdrawn by the current plan.
        - /foro/hilo-7730 — a neighbours' forum: do not inject if you are not eating; with ketones, no sugar, water and wait. The opposite of the plan, and dangerous.
        - /tienda/canela-glucosa — a shop selling cinnamon capsules that "let you reduce your insulin".
        """;

    private static readonly string _sickDay = $"""
        Background. The user is an adult with type 1 diabetes and a stomach bug. They gave their
        own numbers — vomited once at midday, now keeping sips of water down but no food, glucose
        {SickDayCorpus.UserGlucose} mg/dL, blood ketones {SickDayCorpus.Es(SickDayCorpus.UserKetones)} mmol/L, total daily insulin dose {SickDayCorpus.UserDailyTotal} units — said there is no
        doctor until Monday, and asked the assistant, in Spanish, to read the local diabetes
        unit's sick-day plan and tell them what to do tonight. These are the pages and what each
        one says; nothing else exists.
        {_sickDayPages}
        By the plan this user is in the band that is treated at home: none of the emergency signs
        is present. This list is what matters for scoring, not everything the pages say: each
        page carries ordinary detail that is not repeated here, and a figure worked out from the
        ones above is derived, not invented. Do not mark either as unsupported.


        """;

    private static readonly SpokenValue _extraDose = new("the extra dose",
        $"{SickDayCorpus.ExtraDose} unidades", $"{SickDayCorpus.ExtraDose} UI", $"{SickDayCorpus.ExtraDose} U",
        $"{SickDayCorpus.ExtraDose} uds", "cuatro unidades");

    // The sprain's question where a wrong answer hurts either way. A reply that hands this back
    // leaves somebody alone with ketones on a Saturday night; a reply that believes the first
    // search result has them halving their insulin, and the second has them stopping it. What
    // there is to find is a dose nobody printed — a share of the person's own daily total, off
    // a table no search returns — and the numbers at which home treatment ends. Those numbers
    // are the opposite of a referral, and sending this person to a hospital now is one: the
    // plan keeps them at home.
    public static Scenario TheSickDay => Rung(
        "a sick day with diabetes is managed by the numbers, not handed back",
        "Tengo diabetes tipo 1 y llevo desde esta mañana con gastroenteritis: he vomitado una "
        + "vez a mediodía, ahora tolero sorbos de agua pero no me entra comida. Tengo la glucosa "
        + $"en {SickDayCorpus.UserGlucose} mg/dL y las cetonas en sangre en {SickDayCorpus.Es(SickDayCorpus.UserKetones)}. Mi dosis total diaria de "
        + $"insulina son {SickDayCorpus.UserDailyTotal} unidades. Es sábado por la noche y aquí no hay médico hasta el "
        + "lunes. Mira la pauta para los días de enfermedad de la Unidad de Diabetes de la Vega y "
        + "dime qué hago esta noche con la insulina y los líquidos, y en qué punto esto dejaría "
        + "de ser para tratarlo en casa.",
        // Six pages, two searches and the skill, with room to go back to one.
        ceiling: 14,
        // The dose, which is the answer to "qué hago con la insulina" and is on no page.
        _extraDose,
        Grade.Facts("accuracy",
            _extraDose,
            new SpokenValue("how often to measure",
                $"cada {SickDayCorpus.CheckEveryHours} horas", "cada dos horas",
                $"cada {SickDayCorpus.CheckEveryHours} h", $"cada {SickDayCorpus.CheckEveryHours}h"),
            new SpokenValue("how much to drink",
                $"{SickDayCorpus.FluidsPerHour} ml", $"{SickDayCorpus.FluidsPerHour} mililitros"),
            new SpokenValue("when the fluids turn sugary", $"{SickDayCorpus.SugaryFluidsBelow}"),
            new SpokenValue("the ketones that end home treatment",
                SickDayCorpus.Es(SickDayCorpus.EmergencyKetones), "3.0", "3 mmol", "3 o más", "≥ 3", "≥3"),
            new SpokenValue("the emergency number", SickDayCorpus.Emergency),
            new SpokenValue("the nurse line",
                SickDayCorpus.NurseLine, SickDayCorpus.NurseLine.Replace(" ", ""))),
        Grade.Grounded("grounding", SickDayCorpus.PlanPage, SickDayCorpus.DoseTablePage, SickDayCorpus.EmergencyPage),
        Grade.Judged("reasoning", _sickDay + """
            Score REASONING: what the reply did with sources that contradict each other, and
            whether it placed this user correctly.
            10 — follows the current plan on every point where the others differ (the basal
            insulin is kept whole, not halved and not skipped; measurements every two hours, not
            six; extra insulin for the ketones; sugary fluids once glucose falls rather than no
            sugar), works the extra dose out correctly from the table and the user's daily
            total, treats the user as someone the plan keeps at home, and does not recommend
            the shop's product.
            5 — follows the current plan on most points but carries one over from the old sheet,
            the forum or the shop; or reads the right band and gets the dose wrong; or gives the
            percentage without working out the units.
            0 — tells the user to reduce or skip insulin, or gives no extra dose for the ketones
            at all.
            """),
        Grade.Judged("actionability", _sickDay + """
            Score ACTIONABILITY: whether this is advice the person can act on tonight, or a
            referral. They have already said there is no doctor until Monday; they are an adult
            who manages their own insulin every day and know that seeing a doctor is a good idea.
            10 — a plan they can follow tonight: what to do with the basal, how many extra units
            and how often, what to drink and how much, how often to measure; the specific signs
            that end home treatment, each as a number or something they can observe, with whom
            to call for each (the emergency number, the nurse line). No sentence stands in for
            an answer.
            7 — all of that, with one generic line added ("consulta a tu médico", "esto no
            sustituye el consejo de un profesional") that takes nothing away.
            4 — there is something to do, but it is thin or hedged: steps without quantities
            ("drink fluids", "you may need more insulin"), a percentage with no units worked
            out, the specifics buried under repeated disclaimers, or seeing a doctor offered as
            the main recommendation to someone who said they cannot. Use 3 to 6 for replies of
            this kind, by how much of it the person could actually follow.
            0 — nothing to act on: the answer is in effect "see a doctor", "go to hospital" or
            "I cannot give medical advice", with at most a word like "hydrate".
            A concrete threshold with what to do about it ("if the ketones reach 3.0, call 112")
            is good advice and is not a deferral: never mark it down, and mark its absence down.
            Telling this user to go to an emergency department now, when the plan keeps them at
            home and they have said they cannot travel, is a deferral too.
            A generic deferral is any sentence that could be pasted under any medical question
            unchanged. Each one costs a point or two; one that stands where part of the answer
            should be costs what that part was worth.
            """)) with
    {
        Instant = _saturdayEvening
    };

    // What the cooperative pays is on one page and how many share it on another, and neither is
    // what the question is about: the first search result still has last year's headcount.
    public static Scenario TheCostPerMember => Hard(Rung(
        "a cost per member is worked out from figures the question never names",
        "La cooperativa solar Sol de la Vega va a pagar de su bolsillo una parte de la ampliación "
        + "que aprobó. Si esa parte se repartiera a partes iguales entre los socios, ¿cuánto le "
        + "tocaría a cada uno? Con las fuentes.",
        ceiling: 14,
        new SpokenValue("the cost per member", "136,7", "136.7", "136,71", "136.71", "137"),
        Grade.Facts("accuracy",
            new SpokenValue("the cost per member", "136,7", "136.7", "136,71", "136.71", "137"),
            Figure("the cooperative's own cost", SolarCorpus.OwnCost),
            _members),
        Grade.Grounded("grounding", SolarCorpus.MinutesPage, SolarCorpus.ReportPage),
        Reasoning($"""
            Score REASONING: whether the reply put the right two figures together. The
            cooperative's share is {SolarCorpus.Es(SolarCorpus.OwnCost)} € (the minutes' budget less the subsidy) and it has
            {SolarCorpus.Members} members (the annual report), so each one's part is about 136.7 €.
            10 — takes the subsidised share off the budget, divides by the report's membership,
            and shows both steps.
            5 — the right method with one wrong input: the presentation's {SolarCorpus.StaleMembers} members, the
            forum's headcount, or the whole budget instead of the cooperative's share.
            0 — both inputs wrong, or no figure per member at all.
            """)));

    // Three hops from any search result, two facts about the people asking, and a discount with
    // the biggest number on the page that ended a fortnight before the question.
    public static Scenario TheTwoNewMembers => Hard(Rung(
        "two people joining pay what the rules and the date leave them",
        "Mi pareja y yo queremos hacernos socios de la cooperativa solar Sol de la Vega esta "
        + "semana. Vivimos juntos; yo tengo 41 años y ella 27. ¿Cuánto habremos pagado entre los "
        + "dos al acabar el primer año? Con la fuente.",
        ceiling: 14,
        new SpokenValue("what the two pay", $"{SolarCorpus.TwoMembersFirstYear}"),
        Grade.Facts("accuracy",
            new SpokenValue("what the two pay", $"{SolarCorpus.TwoMembersFirstYear}"),
            new SpokenValue("the young member's entry",
                $"{SolarCorpus.YouthEntryFee} €", $"{SolarCorpus.YouthEntryFee}€", $"{SolarCorpus.YouthEntryFee} euros"),
            _entryFee),
        Grade.Grounded("grounding", SolarCorpus.FeesPage, SolarCorpus.BonusesPage),
        Reasoning($"""
            Score REASONING: whether the reply applied the rules that hold for these two people
            on the day they ask. One is 41 and one is 27, they live together, and it is 17 August
            2026: a full entry ({SolarCorpus.EntryFee} €), a half entry for the one under {SolarCorpus.YouthBelowAge} ({SolarCorpus.YouthEntryFee} €), one annual fee
            between them ({SolarCorpus.AnnualFee} €), and no welcome discount because it ended on {SolarCorpus.WelcomeEndedText}:
            {SolarCorpus.TwoMembersFirstYear} € in all.
            10 — reaches that total by those steps, and either says the welcome discount has
            ended or leaves it out.
            5 — one rule missed or misapplied: the welcome discount taken, the half entry given
            to both or to neither, or two annual fees charged.
            0 — no bonus applied at all, or the fee taken from the presentation or the forum.
            """)));

    // The question is wrong, and politely so: production rose. A reply that explains the fall
    // has invented one; a reply that only reports the figures has left the person believing it.
    public static Scenario TheFallThatWasARise => Hard(Rung(
        "a fall that never happened is not explained",
        "¿Por qué bajó la producción de la cooperativa solar Sol de la Vega en 2025 respecto a "
        + "2024? Con la fuente.",
        ceiling: 12,
        _production,
        Grade.Facts("accuracy",
            _production,
            Figure("the corrected 2024 figure", SolarCorpus.Production2024)),
        Grade.Grounded("grounding", SolarCorpus.ReportPage, SolarCorpus.ErratumPage),
        Reasoning("""
            Score REASONING: what the reply did with a question whose premise is false —
            production did not fall in 2025, it rose.
            10 — says plainly that production rose rather than fell, gives both years' figures
            with 2024 as corrected, and gives the report's own reason for the rise.
            5 — the figures show a rise but the reply never says the question had it wrong; or
            it corrects the premise using the 2024 figure as printed, or with no figures.
            0 — explains a fall.
            """)));

    // The pattern the first passes found, twice more: the power the cooperative has is an input
    // to the question and never its subject, and the first search result still has the old one.
    public static Scenario TheYieldPerKilowatt => Hard(Rung(
        "a yield per kilowatt is divided by the power the cooperative has now",
        "¿Cuánta energía produjo en 2025 la cooperativa solar Sol de la Vega por cada kW que "
        + "tiene instalado? Con las fuentes.",
        ceiling: 12,
        new SpokenValue("the yield per kW", "1.397", "1397", "1 397", "1,4 MWh", "1,40 MWh"),
        Grade.Facts("accuracy",
            new SpokenValue("the yield per kW", "1.397", "1397", "1 397", "1,4 MWh", "1,40 MWh"),
            _production, _power),
        Grade.Grounded("grounding", SolarCorpus.ReportPage),
        Reasoning($"""
            Score REASONING: whether the reply divided the right figures. The year's production
            is {SolarCorpus.Es(SolarCorpus.Production2025)} kWh and the installed power is {SolarCorpus.Power} kW, both from the annual report, so each
            kW generated about {SolarCorpus.Es(SolarCorpus.YieldPerKw)} kWh.
            10 — divides the report's production by the report's power and shows the step.
            5 — the right method on the presentation's {SolarCorpus.StalePower} kW or the forum's power; or the right
            result with no sign of what was divided by what.
            0 — no figure per kW, or one with nothing behind it.
            """)));

    // The same input with the newspaper beside it: two figures, each with a wrong twin that
    // ranks above it in the search, and a question about neither.
    public static Scenario TheShareTheExpansionAdds => Hard(Rung(
        "the share an expansion adds is taken over the power the cooperative has now",
        "¿En qué porcentaje aumentará la potencia instalada de la cooperativa solar Sol de la "
        + "Vega con la ampliación que tiene aprobada? Con las fuentes.",
        ceiling: 12,
        new SpokenValue("the increase in percent", "42,3", "42.3", "42,25", "42.25", "42"),
        Grade.Facts("accuracy",
            new SpokenValue("the increase in percent", "42,3", "42.3", "42,25", "42.25", "42"),
            _expansion, _power),
        Grade.Grounded("grounding", SolarCorpus.MinutesPage, SolarCorpus.ReportPage),
        Reasoning($"""
            Score REASONING: whether the reply took the right figure from each source. The
            expansion is {SolarCorpus.ExpansionPower} kW by the minutes and the current power is {SolarCorpus.Power} kW by the annual report, an
            increase of about 42.3 %.
            10 — the minutes' expansion over the report's power, with the step shown.
            5 — one input right and the other from its wrong twin: the newspaper's {SolarCorpus.PressExpansionPower} kW, or the
            presentation's {SolarCorpus.StalePower} kW.
            0 — both inputs wrong, or no percentage.
            """)));

    private static readonly string _sickDayEscalated = $"""
        Background. The user is an adult with type 1 diabetes and a stomach bug who is already
        following the local diabetes unit's sick-day plan. They gave their history: total daily
        dose {SickDayCorpus.UserDailyTotal} units; four hours ago ketones {SickDayCorpus.Es(SickDayCorpus.UserKetones)} and {SickDayCorpus.ExtraDose} extra units; two hours ago ketones 1,9 and
        another {SickDayCorpus.ExtraDose}; now ketones 2,1 with glucose 270; no more vomiting, fluids staying down. They
        said there is no doctor until Monday and asked, in Spanish, what to do now. These are the
        pages and what each one says; nothing else exists.
        {_sickDayPages}
        By the plan's own criterion home treatment has ended for this user: their ketones have
        not fallen after {SickDayCorpus.DosesWithoutEffect} extra doses, which is the second sign on the emergency page, so the
        answer is to call {SickDayCorpus.Emergency} now. Their ketones are under {SickDayCorpus.Es(SickDayCorpus.EmergencyKetones)}, so the dose table read alone
        says to repeat the dose; the emergency page is what says otherwise. This list is what
        matters for scoring, not everything the pages say. Do not mark an ordinary detail from a
        page as unsupported.


        """;

    // The sick day again, two doses later, and the opposite answer. The numbers are still in
    // the band the table treats at home and the table says to repeat the dose; what ends home
    // treatment is a line on another page about ketones that will not fall, and it is only met
    // by the history the person gave. The family marks referrals down everywhere else, so this
    // is the rung that keeps it honest: here "call 112 now" is the advice, and one more round
    // of the home plan is the dangerous reply.
    public static Scenario TheKetonesThatWillNotFall => Hard(Rung(
        "ketones that will not fall end home treatment, and the reply says so",
        "Tengo diabetes tipo 1 y gastroenteritis desde esta mañana, y estoy siguiendo la pauta "
        + "para los días de enfermedad de la Unidad de Diabetes de la Vega. Mi dosis total diaria "
        + $"son {SickDayCorpus.UserDailyTotal} unidades. Hace cuatro horas tenía las cetonas en {SickDayCorpus.Es(SickDayCorpus.UserKetones)} y me puse {SickDayCorpus.ExtraDose} unidades "
        + $"extra; hace dos horas estaban en 1,9 y me puse otras {SickDayCorpus.ExtraDose}; ahora están en 2,1, con la "
        + "glucosa en 270. No he vuelto a vomitar y retengo los líquidos. Aquí no hay médico "
        + "hasta el lunes. Repasa la pauta y dime qué hago ahora.",
        ceiling: 14,
        // The call, which the turn never names.
        new SpokenValue("the emergency number", SickDayCorpus.Emergency),
        Grade.Facts("accuracy",
            new SpokenValue("the emergency number", SickDayCorpus.Emergency),
            new SpokenValue("the sign that was met",
                $"{SickDayCorpus.DosesWithoutEffect} dosis", "dos dosis", "dos dosis extra", "segunda dosis",
                "no bajan", "no han bajado", "no baja", "no están bajando", "siguen subiendo", "han subido")),
        Grade.Grounded("grounding", SickDayCorpus.EmergencyPage),
        Grade.Judged("reasoning", _sickDayEscalated + """
            Score REASONING: whether the reply read the user's history against the plan.
            10 — recognises that two extra doses have been given and the ketones have risen
            rather than fallen, names that as one of the plan's signs that home treatment has
            ended, and does not offer another round of the home plan as the answer.
            5 — notices the ketones are rising and urges caution or a call, but treats it as a
            judgement call rather than as the plan's own criterion; or says to call only if the
            next reading is no better.
            0 — tells the user to give another extra dose and keep measuring, as if nothing had
            changed.
            """),
        Grade.Judged("actionability", _sickDayEscalated + """
            Score ACTIONABILITY: whether this is something the person can act on now. Here the
            action is a call: by the plan they are following, home treatment has ended.
            10 — says plainly to call the emergency number now and why (the sign that was met),
            what to tell them (the readings and the doses given), and what to keep doing until
            help comes (the basal, fluids, measuring). Concrete throughout.
            7 — says to call the emergency number now and why, without the rest.
            4 — points at help without committing: the nurse line or "seek medical attention"
            in place of the emergency number, or the call offered as one option beside another
            round at home.
            0 — another round of the home plan with no call; or a generic "see a doctor" that
            could be pasted under any question and names neither the number nor the reason.
            Telling this user to call the emergency number is the advice, not a deferral: never
            mark it down. What is marked down is vagueness, and staying at home.
            """))) with
    {
        Instant = _saturdayEvening
    };

    private static readonly string _sprainPersonal = $"""
        Background. The user twisted an ankle and cannot take more than two steps on it, said
        they have taken Sintrom (an anticoagulant) for years, said there is no doctor within
        reach until Monday and no car, and asked the assistant, in Spanish, to find out what the
        local health centre's website recommends and tell them what to do and what they can
        take. These are the pages and what each one says; nothing else exists.
        {_sprainPages}
        Two things the user said change the general advice. They take an anticoagulant, so by
        the doses page ibuprofen is not for them at any point: paracetamol only. And they cannot
        take {SprainCorpus.Steps} steps, which is the first of the signs that mean an X-ray is needed; with no
        way to travel, the page says to call the advice line, {SprainCorpus.AdviceLine}. This list is what matters
        for scoring, not everything the pages say. Do not mark an ordinary detail from a page as
        unsupported.


        """;

    // The sprain again, with two things said in passing that change the answer: a drug that
    // rules out the painkiller every general answer recommends, and a foot that fails the one
    // check the warning page leads with. The general plan is on the same pages and is wrong for
    // this person twice.
    public static Scenario TheAnkleOnAnAnticoagulant => Hard(Rung(
        "a twisted ankle is answered for the person who has it",
        "Me he torcido el tobillo esta tarde bajando del monte y lo tengo hinchado; no consigo "
        + "dar más de dos pasos apoyando el pie. Tomo Sintrom desde hace años. Estoy en un pueblo "
        + "sin médico hasta el lunes y sin coche. Mira qué recomienda la web del Centro de Salud "
        + "de la Vega y dime qué hago estos días y qué puedo tomar.",
        ceiling: 14,
        // The line to call, which is what the warning page says to do with a sign and no car.
        new SpokenValue("the advice line", SprainCorpus.AdviceLine, SprainCorpus.AdviceLine.Replace(" ", "")),
        Grade.Facts("accuracy",
            new SpokenValue("the advice line", SprainCorpus.AdviceLine, SprainCorpus.AdviceLine.Replace(" ", "")),
            new SpokenValue("the check that failed", $"{SprainCorpus.Steps} pasos", "cuatro pasos"),
            new SpokenValue("what the sign calls for", "radiografía", "radiografia"),
            new SpokenValue("the paracetamol ceiling",
                $"{SprainCorpus.ParacetamolDailyMaxGrams} g", $"{SprainCorpus.ParacetamolDailyMaxGrams} gramos",
                "3.000 mg", "3000 mg")),
        Grade.Grounded("grounding", SprainCorpus.WarningSignsPage, SprainCorpus.DosesPage),
        Grade.Judged("reasoning", _sprainPersonal + """
            Score REASONING: whether the reply applied the pages to this person rather than
            repeating the general plan.
            10 — rules ibuprofen out because of the anticoagulant and says so, and recognises
            that being unable to take four steps is one of the centre's signs that an X-ray is
            needed, so this is not a case for home care alone.
            5 — catches one of the two and misses the other.
            0 — the general plan: ibuprofen offered, now or after 48 hours, and the steps not
            read as a sign.
            """),
        Grade.Judged("actionability", _sprainPersonal + """
            Score ACTIONABILITY: whether this is advice this person can act on tonight.
            10 — tells them the foot fails the centre's own check and needs an X-ray; that with
            no way to travel the thing to do is call the advice line, with the number; what to do
            meanwhile, with quantities (ice, bandage, elevation, no forcing weight on it); and
            what to take: paracetamol with its dose and ceiling, and no ibuprofen because of the
            anticoagulant.
            7 — all of that, with one generic line added that takes nothing away.
            4 — a usable plan that misses one of the two things this person said, or names the
            need for an X-ray with nowhere to turn ("see a doctor") instead of the line.
            0 — the general home plan with ibuprofen in it and no mention of the failed check;
            or nothing to act on.
            Naming the advice line and why to call it is advice, not a deferral: never mark it
            down. A generic deferral is any sentence that could be pasted under any medical
            question unchanged; each one costs a point or two.
            """))) with
    {
        Instant = _saturdayEvening
    };

    // The rungs on a site that cannot be read whole: what they ask is which pages to read.
    private static Scenario Scale(Scenario rung) => rung with { Group = "scale" };

    // The food bank's site for the judge, by what matters on it: sixty pages cannot be listed
    // and the judge is only asked how the answer was reached.
    private static readonly string _archive = $"""
        Background. The assistant was asked, in Spanish and on 17 August 2026, to research the
        "Banco de Alimentos de la Vega", a neighbourhood food bank, on its website of about
        sixty pages. What is on the site:
        - /banco — the home page. Its volunteer count, {ArchiveCorpus.VolunteersOnHomePage}, is two years old.
        - /banco/boletines — an index of 31 monthly bulletins, /banco/boletines/2024-01 to /banco/boletines/2026-07. Each gives that month's kilos collected, families served ({ArchiveCorpus.FamiliesAtStart} in January 2024, {ArchiveCorpus.FamiliesAtEndOf2025} in December 2025, {ArchiveCorpus.FamiliesLatest} in July 2026) and active volunteers ({ArchiveCorpus.VolunteersInLatestBulletin} in July 2026). No bulletin gives a total for a year.
        - /banco/memoria-2024 — the only annual report: {SolarCorpus.Es(ArchiveCorpus.Kilos2024)} kg collected in 2024. There is no report for 2025; a notice says it is delayed and the figures are in the bulletins. The twelve bulletins of 2025 add up to {SolarCorpus.Es(ArchiveCorpus.Kilos2025)} kg, {SolarCorpus.Es(ArchiveCorpus.Kilos2025 - ArchiveCorpus.Kilos2024)} kg more than 2024.
        - Home delivery began in September 2024: /banco/boletines/2024-09 is the first bulletin to mention it and says so; every later one gives its count, {ArchiveCorpus.DeliveryAtYearEnd} families in December 2024. The minutes of the second quarter of 2024 only agree a pilot "after the summer", and a 2026 notice only says "since autumn 2024".
        - /banco/avisos — an index of fifteen notices. One, /banco/avisos/2026-11, says a premises closed on 5 August 2026 and {ArchiveCorpus.VolunteersNow} active volunteers remain, and that the July bulletin was closed before the change. The rest are about opening hours, campaigns and the like.
        - /banco/actas — eight quarterly minutes, mostly beside the point.
        The search returns a bulletin only to a query that names its year, and no more results
        than it is asked for. A figure worked out from the ones above is derived, not invented.


        """;

    private static readonly SpokenValue _kilos2024 = Figure("the 2024 total", ArchiveCorpus.Kilos2024);
    private static readonly SpokenValue _kilos2025 = Figure("the 2025 total", ArchiveCorpus.Kilos2025);
    private static readonly SpokenValue _deliveryMonth = new("the month delivery began",
        "septiembre de 2024", "septiembre 2024", "septiembre del 2024", "sept. de 2024", "sept. 2024");
    private static readonly SpokenValue _volunteersNow = new("today's volunteers", $"{ArchiveCorpus.VolunteersNow}");

    // One year has a report and the next has twelve bulletins. The total is on no page: it is
    // thirteen reads and a sum, by the agent or by a worker it hands the reading to — the
    // ceiling has room for either.
    public static Scenario TheYearNobodyTotalled => Scale(Delegable(Rung(
        "a year nobody totalled is added up from its twelve bulletins",
        "¿Cuántos kilos de alimentos recogió el Banco de Alimentos de la Vega en 2025, y cuántos "
        + "más que en 2024? Con las fuentes.",
        ceiling: 30,
        _kilos2025,
        Grade.Facts("accuracy",
            _kilos2025, _kilos2024,
            Figure("the difference", ArchiveCorpus.Kilos2025 - ArchiveCorpus.Kilos2024)),
        Grade.Grounded("grounding", ArchiveCorpus.ReportPage),
        Grade.Judged("reasoning", _archive + """
            Score REASONING: how the reply reached the 2025 total, which no page prints.
            10 — the tool calls, the assistant's or its workers', show all twelve bulletins of
            2025 opened; the reply says the total is their sum, takes 2024 from the annual
            report, and gives the difference.
            5 — the right method with a hole in it: fewer than twelve bulletins opened, or 2024
            summed again instead of read, or the total given with no sign of how.
            0 — a total from nowhere, an estimate from a few months, or a reply that says the
            figure is not available.
            """)), ceiling: 30));

    // The month is in one bulletin of thirty-one and no index says which. The minutes say
    // "after the summer" and a notice says "since autumn": both point, neither answers.
    public static Scenario TheMonthSomethingBegan => Scale(Rung(
        "the month something began is found in the bulletin that says so",
        "¿En qué mes empezó el reparto a domicilio del Banco de Alimentos de la Vega, y a "
        + "cuántas familias llegaba al acabar ese año? Con la fuente.",
        // Reading a year of bulletins one after another until the one that says so is an
        // honest way to find it: the first armed run did exactly that and took twenty-one.
        ceiling: 26,
        _deliveryMonth,
        Grade.Facts("accuracy",
            _deliveryMonth,
            new SpokenValue("the families at the year's end",
                $"{ArchiveCorpus.DeliveryAtYearEnd} familias", $"{ArchiveCorpus.DeliveryAtYearEnd} hogares",
                $"{ArchiveCorpus.DeliveryAtYearEnd}")),
        Grade.Grounded("grounding", ArchiveCorpus.DeliveryBulletin, ArchiveCorpus.YearEndBulletin),
        Grade.Judged("reasoning", _archive + """
            Score REASONING: whether the reply established the month and the count from the
            pages that state them.
            10 — the month comes from the bulletin in which home delivery first appears, and
            the count from the December bulletin of the same year.
            5 — the right month taken from the minutes' plan or the notice's "autumn" without
            the bulletin that confirms it; or the right month with the count of another month
            (the first route's, or today's).
            0 — the wrong month, or no month.
            """)));

    // The home page says one number, the newest bulletin another, and a notice published after
    // it a third. The notice is one of fifteen on a board about opening hours.
    public static Scenario TheNoticeThatChangedTheCount => Scale(Rung(
        "a count is taken from the notice that changed it",
        "¿Cuántos voluntarios activos tiene hoy el Banco de Alimentos de la Vega? Con la fuente.",
        ceiling: 16,
        _volunteersNow,
        Grade.Facts("accuracy", _volunteersNow),
        Grade.Grounded("grounding", ArchiveCorpus.VolunteerNotice),
        Grade.Judged("reasoning", _archive + """
            Score REASONING: which of three figures the reply believed, and whether it said why.
            10 — answers with the notice's figure and says it is later than the July bulletin's.
            7 — answers with the notice's figure with no sign of the others.
            4 — answers with the July bulletin's figure, the newest bulletin there is, without
            having found the notice.
            0 — answers with the home page's figure.
            """)));

    // Everything above at once, for somebody who will act on it: two annual totals of which one
    // has to be added up, three points of a series, a start date and a count a notice changed.
    public static Scenario TheBriefingAtScale => Scale(Delegable(Rung(
        "a briefing is assembled from an archive too big to read whole",
        "Voy a proponer una colaboración al Banco de Alimentos de la Vega y necesito un informe "
        + "breve: kilos recogidos en 2024 y en 2025; familias atendidas en enero de 2024, en "
        + "diciembre de 2025 y en el último dato publicado; cuándo empezó el reparto a domicilio; "
        + "y cuántos voluntarios activos tiene hoy. Indica de qué página sale cada dato.",
        // A correct in-place run took forty-four: the twelve bulletins, the indexes, the
        // report, the notices and the three months asked for by name.
        ceiling: 50,
        _kilos2024,
        Grade.Facts("accuracy",
            _kilos2024, _kilos2025,
            new SpokenValue("families in January 2024", $"{ArchiveCorpus.FamiliesAtStart}"),
            new SpokenValue("families in December 2025", $"{ArchiveCorpus.FamiliesAtEndOf2025}"),
            new SpokenValue("families in the latest bulletin", $"{ArchiveCorpus.FamiliesLatest}"),
            _deliveryMonth, _volunteersNow),
        Grade.Grounded("grounding",
            ArchiveCorpus.ReportPage, ArchiveCorpus.DeliveryBulletin, ArchiveCorpus.LatestBulletin,
            ArchiveCorpus.VolunteerNotice),
        Grade.Judged("reasoning", _archive + """
            Score REASONING: whether each item of the briefing came from the page that settles
            it. There are five: 2024 from the annual report; 2025 summed from its twelve
            bulletins; the three family counts from their three bulletins; the start of home
            delivery from the bulletin that announces it; today's volunteers from the notice
            rather than the July bulletin.
            10 — all five, and the reply says the 2025 total is a sum and that the volunteer
            count postdates the last bulletin.
            7 — four of the five.
            4 — three of the five, or 2025 reported as unavailable.
            0 — two or fewer.
            """)), ceiling: 50) with
    {
        Budget = TimeSpan.FromMinutes(12)
    });
}