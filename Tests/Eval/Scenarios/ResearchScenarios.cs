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
//   grounding  whether each figure is tied to the page it came from;
//   reasoning  what the reply did with sources that disagree, a number nobody printed, or a
//              question nothing answers.
//
// A rung still passes or fails, on the little that is a contract: it searched, it stayed inside
// its ceiling, it finished, and its headline figure is in the reply. The marks are taken either
// way.
//
// Six rungs take the agent's workers away (`WorkerMode.None`) so the score is what the model
// finds by itself; the prompt lets research go to a worker, and a rung that left that to the
// model's coin would be measuring the coin. The last two give the workers back, real ones, on
// the same model — a worker runs its parent's — and take whichever route the model chooses.
public static class ResearchScenarios
{
    public static IReadOnlyList<Scenario> All =>
    [
        TheCurrentFigures, TheGrowth, TheFees, TheSavingNobodyPublished,
        TheApprovedExpansion, TheBestQuarter, ThePlannedTotal, TheBriefing
    ];

    // Everything the judge has to know, because it is shown the reply and the calls and never a
    // page. Written from the corpus's own constants, so the rubric cannot disagree with the site.
    private static readonly string _corpus = $"""
        Background. The assistant was asked, in Spanish, to research a neighbourhood solar
        cooperative called "Sol de la Vega" on a small website. These are the pages and what each
        one says; nothing else exists.
        - /solar — presentation, published May 2024, the first search result: {SolarCorpus.StaleMembers} members, {SolarCorpus.StalePower} kW, entry contribution {SolarCorpus.StaleEntryFee} €. All of it is out of date.
        - /solar/memoria-2025 — the cooperative's annual report, published March 2026: {SolarCorpus.Members} members and {SolarCorpus.Power} kW at the end of 2025; {SolarCorpus.Es(SolarCorpus.Production2025)} kWh generated in 2025 against {SolarCorpus.Es(SolarCorpus.Production2024)} kWh in 2024 (a rise of {SolarCorpus.Es(SolarCorpus.Growth)} kWh, about 50.8 %).
        - /solar/cuotas — the fees in force since January 2026, in no search result and reachable only by a link from the two pages above: entry contribution {SolarCorpus.EntryFee} €, annual fee {SolarCorpus.AnnualFee} € from the first year ({SolarCorpus.FirstYearCost} € by the end of the first year).
        - /solar/produccion-2025 — the monthly production table for 2025. The best quarter is the third (July to September) with {SolarCorpus.Es(SolarCorpus.BestQuarter)} kWh, 34.5 % of the year; the page states neither.
        - /solar/acta-2026-06 — minutes of the general assembly of 12 June 2026, the primary source for its decisions: an expansion of {SolarCorpus.ExpansionPower} kW on the school roof, budget {SolarCorpus.Es(SolarCorpus.Budget)} €, {SolarCorpus.SubsidyPercent} % of it subsidised (so the cooperative pays {SolarCorpus.Es(SolarCorpus.OwnCost)} € and will have {SolarCorpus.PlannedPower} kW in total), approved by {SolarCorpus.VotesFor} votes for, {SolarCorpus.VotesAgainst} against and {SolarCorpus.Abstentions} abstentions.
        - /diario/ampliacion-solar — a local newspaper's account of the same assembly: it says {SolarCorpus.PressExpansionPower} kW, which is wrong, and the same budget.
        - /foro/hilo-8841 — a neighbours' forum thread: "more than {SolarCorpus.ForumMembers} members", "about {SolarCorpus.ForumPower} kW", "the fee is {SolarCorpus.ForumFee} €", and one neighbour who "saves about {SolarCorpus.ForumSaving} € a year". Unverified impressions, all of them wrong where they can be checked.
        No page gives an average saving per member, and no page says the newspaper is wrong.


        """;

    private static Grade Grounding => Grade.Judged("grounding", _corpus + """
        Score GROUNDING: how well the reply ties what it states to where it came from. Judge only
        this, not whether the figures are right.
        10 — every figure the reply states is attributed to a specific page (by its name or its
        url), and the tool calls show that page was opened.
        5 — sources are listed but not tied to particular figures, or about half of the figures
        are attributed.
        0 — no source is given, or figures are attributed to pages that were never opened.
        A reply that states no figure because it found none is scored on whether it says where it
        looked.
        """);

    private static Grade Reasoning(string asked) => Grade.Judged("reasoning", _corpus + asked);

    private static Scenario Rung(string name, string turn, int ceiling, SpokenValue headline, params Grade[] graded) =>
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
            Reply = new ReplyExpectation { Mentions = [headline] },
            Graded = graded,
            // Every run, always: the marks are a mean, and a mean over two runs on one model and
            // three on another is not a comparison.
            Policy = new RunPolicy(2, 3) { Exhaustive = true },
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
        ceiling: 10,
        _members,
        Grade.Facts("accuracy", _members, _power),
        Grounding,
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

    // Both years are on one page and the answer is on none: a difference and a percentage.
    public static Scenario TheGrowth => Rung(
        "a growth figure is derived from two years' production",
        "¿Cuánto creció la producción de la cooperativa solar Sol de la Vega de 2024 a 2025? Lo "
        + "quiero en kWh y en porcentaje, con la fuente.",
        ceiling: 10,
        Figure("the growth in kWh", SolarCorpus.Growth),
        Grade.Facts("accuracy",
            Figure("the growth in kWh", SolarCorpus.Growth),
            new SpokenValue("the growth in percent", "50,8", "50.8", "50,76", "50.76", "51")),
        Grounding,
        Reasoning("""
            Score REASONING: whether the reply derived the growth correctly and showed how.
            10 — states both years' production, the difference and the percentage, computed over
            the 2024 figure, all correct.
            5 — the difference is right but the percentage is missing, wrong, or computed over
            the 2025 figure; or the results are right with no sign of the two figures they came
            from.
            0 — no derivation: a figure with nothing behind it, or the wrong years.
            """));

    // The fees are on a page no search returns. The page that does rank carries last year's
    // fee and a link; the forum carries a fee that was never the fee.
    public static Scenario TheFees => Rung(
        "a fee is found on a page no search surfaces",
        "Quiero hacerme socio de la cooperativa solar Sol de la Vega. ¿Cuánto me costaría entrar "
        + "y cuánto habría pagado en total al acabar el primer año? Con la fuente.",
        ceiling: 12,
        _entryFee,
        Grade.Facts("accuracy",
            _entryFee, _annualFee,
            new SpokenValue("the first year's total", $"{SolarCorpus.FirstYearCost}")),
        Grounding,
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
        // Absence is only established by looking, and looking takes more pages than finding.
        ceiling: 14,
        // The subject, so the reply is about the question that was asked; what it says about
        // it is the marks' business.
        new SpokenValue("the subject", "ahorro", "ahorra", "ahorran", "ahorrar"),
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
        Grounding,
        Reasoning("""
            Score REASONING: whether the reply told an anecdote from a datum and looked far
            enough before concluding.
            10 — the tool calls show the cooperative's own pages were opened (the report at
            least), and the reply distinguishes what the cooperative publishes from what one
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
        ceiling: 10,
        _expansion,
        Grade.Facts("accuracy",
            _expansion, _budget,
            new SpokenValue("the votes for", $"{SolarCorpus.VotesFor}"),
            new SpokenValue("the votes against", $"{SolarCorpus.VotesAgainst}")),
        Grounding,
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
        ceiling: 12,
        Figure("the best quarter's energy", SolarCorpus.BestQuarter),
        Grade.Facts("accuracy",
            new SpokenValue("the third quarter",
                "tercer trimestre", "tercero", "T3", "3T", "Q3", "3.er trimestre", "3er trimestre",
                "julio a septiembre", "julio-septiembre", "julio, agosto y septiembre",
                "julio–septiembre"),
            Figure("the best quarter's energy", SolarCorpus.BestQuarter),
            new SpokenValue("its share of the year", "34,5", "34.5", "34,48", "34.48", "34")),
        Grounding,
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
    public static Scenario ThePlannedTotal => Delegable(Rung(
        "a planned total is put together across the report and the minutes",
        "Cuando esté hecha la ampliación que aprobó la cooperativa solar Sol de la Vega, ¿qué "
        + "potencia total tendrá y cuánto tendrá que poner de su bolsillo, descontada la "
        + "subvención? Con las fuentes.",
        ceiling: 14,
        new SpokenValue("the planned total", $"{SolarCorpus.PlannedPower}"),
        Grade.Facts("accuracy",
            new SpokenValue("the planned total", $"{SolarCorpus.PlannedPower}"),
            Figure("the cooperative's own cost", SolarCorpus.OwnCost)),
        Grounding,
        Reasoning("""
            Score REASONING: whether the reply combined the right figure from each source.
            10 — adds the report's current power to the minutes' approved expansion, takes the
            subsidised share off the budget, and shows both steps.
            5 — one of the two results is right and the other is missing or built on a wrong
            input (the presentation's old power, the newspaper's expansion); or both are right
            with no sign of what was added to what.
            0 — both results are built on the wrong inputs, or neither is given.
            """)), ceiling: 14);

    // The whole site at once, and the instruction a careful person gives: say where each thing
    // comes from, and say what does not add up.
    public static Scenario TheBriefing => Delegable(Rung(
        "a briefing is assembled across the whole site",
        "Estoy pensando en hacerme socio de la cooperativa solar Sol de la Vega. Investígala a "
        + "fondo y hazme un informe breve: cuántos socios y cuánta potencia tiene hoy, cuánto "
        + "produjo en 2025, qué ampliación tiene aprobada y con qué presupuesto, y cuánto cuesta "
        + "entrar. Indica de qué página sale cada dato y avísame de cualquier cifra que no cuadre "
        + "entre fuentes.",
        ceiling: 20,
        _members,
        Grade.Facts("accuracy", _members, _power, _production, _expansion, _budget, _entryFee, _annualFee),
        Grounding,
        Reasoning("""
            Score REASONING: whether the briefing noticed what does not add up, as it was asked
            to. There are three discrepancies: the presentation's figures are older than the
            report's (members, power, entry fee); the newspaper's expansion is larger than the
            minutes'; the forum's figures match nothing official.
            10 — reports at least two of the three, and in each case says which source it
            believed and why.
            5 — reports one, or mentions conflicting figures without saying which to believe.
            0 — reports none, or resolves one in favour of the wrong source.
            """)), ceiling: 20);
}