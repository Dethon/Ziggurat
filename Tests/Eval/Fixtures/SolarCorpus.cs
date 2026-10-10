using System.Globalization;
using Microsoft.AspNetCore.Builder;

namespace Tests.Eval.Fixtures;

// The research family's corpus: one subject — a neighbourhood solar cooperative — told by seven
// pages that do not agree. It is served by the same site as everything else so it holds still,
// and it is built around what research is rather than around what a page read is:
//
//   - an old page that ranks first and still carries last year's figures;
//   - a forum thread whose numbers are a neighbour's impression;
//   - a newspaper that got the size of a decision wrong, beside the minutes that took it;
//   - a fees page no search returns, reached only by a link from a page that is;
//   - a monthly table whose interesting answers are sums nobody printed;
//   - and a question the whole corpus cannot answer, with an anecdote where the figure would be.
//
// Every number a scenario marks a reply against is a constant here, and the derived ones are
// derived here, so the truth is one definition and a page cannot drift from it.
public static class SolarCorpus
{
    // What the 2024 presentation says, and the search snippet with it. True once.
    public const int StaleMembers = 184;
    public const int StalePower = 96;
    public const int StaleEntryFee = 100;

    // What the 2025 annual report says: the figures that are current.
    public const int Members = 237;
    public const int Power = 142;
    // The year before, twice: as the report prints it, and as a later correction puts it. The
    // report still shows the first; everything derived from 2024 follows the second.
    public const int Production2024AsPrinted = 131_600;
    public const int Production2024 = 136_100;
    public const int Production2025 = 198_400;
    public const int Growth = Production2025 - Production2024;

    // What each installed kW generated in the year: a figure no page prints, and one a reply
    // gets wrong by dividing by the first search result's power.
    public const int YieldPerKw = Production2025 / Power;

    // The report's year, month by month. The third quarter is the best one and nothing says so.
    public static readonly IReadOnlyList<int> Monthly2025 =
        [8_900, 11_200, 15_600, 18_300, 21_400, 23_100, 24_900, 23_700, 19_800, 14_200, 9_200, 8_100];

    public const int BestQuarter = 24_900 + 23_700 + 19_800;

    // The fees page, which no search returns.
    public const int EntryFee = 120;
    public const int AnnualFee = 18;
    public const int FirstYearCost = EntryFee + AnnualFee;

    // The bonuses page, two links from any search result. A member under thirty pays half the
    // entry, a household pays one annual fee the first year, and the welcome discount ended
    // before the day the family is asked on.
    public const int YouthBelowAge = 30;
    public const int YouthEntryFee = EntryFee / 2;
    public const int WelcomeDiscount = 30;
    public static readonly DateOnly WelcomeEnded = new(2026, 7, 31);
    public const string WelcomeEndedText = "31 de julio de 2026";
    public const int TwoMembersFirstYear = EntryFee + YouthEntryFee + AnnualFee;

    // What the assembly approved, by its own minutes — and what the newspaper printed instead.
    public const int ExpansionPower = 60;
    public const int PressExpansionPower = 80;
    public const int Budget = 54_000;
    public const int SubsidyPercent = 40;
    public const int OwnCost = Budget * (100 - SubsidyPercent) / 100;
    public const int PlannedPower = Power + ExpansionPower;
    public const int VotesFor = 61;
    public const int VotesAgainst = 13;
    public const int Abstentions = 4;

    // The forum's figures: a headcount and a power nobody published, a fee that was never the
    // fee, and one neighbour's saving standing where an average would be. No page gives one.
    public const int ForumMembers = 300;
    public const int ForumPower = 200;
    public const int ForumFee = 50;
    public const int ForumSaving = 200;

    // The way Spanish writes a number of this size, spelled by hand so that a four-digit month
    // reads the same on every machine: the framework's own answer depends on its culture data.
    public static string Es(int number) =>
        number.ToString("#,0", CultureInfo.InvariantCulture).Replace(',', '.');

    private static readonly string[] _months =
    [
        "Enero", "Febrero", "Marzo", "Abril", "Mayo", "Junio",
        "Julio", "Agosto", "Septiembre", "Octubre", "Noviembre", "Diciembre"
    ];

    public static void Map(WebApplication app)
    {
        app.MapGet("/solar", () => EvalWeb.Html(Presentation()));
        app.MapGet("/solar/memoria-2025", () => EvalWeb.Html(Report()));
        app.MapGet("/solar/cuotas", () => EvalWeb.Html(Fees()));
        app.MapGet("/solar/bonificaciones", () => EvalWeb.Html(Bonuses()));
        app.MapGet("/solar/fe-de-erratas", () => EvalWeb.Html(Erratum()));
        app.MapGet("/solar/produccion-2025", () => EvalWeb.Html(Production()));
        app.MapGet("/solar/acta-2026-06", () => EvalWeb.Html(Minutes()));
        app.MapGet("/diario/ampliacion-solar", () => EvalWeb.Html(Press()));
        app.MapGet("/foro/hilo-8841", () => EvalWeb.Html(Forum()));
    }

    // In this order on purpose: the stale page first, the forum and the newspaper above the
    // report and the minutes. A reader who takes the top of the list takes the wrong figure, and
    // the snippets of the two sources that are right carry no figure at all.
    internal static IReadOnlyList<SearchEntry> Search(string baseUrl) =>
    [
        new(["cooperativa", "solar", "vega", "socio"],
            "Sol de la Vega: la cooperativa solar del vecindario",
            $"{baseUrl}/solar",
            $"Somos {StaleMembers} socios y tenemos {StalePower} kW instalados en la cubierta del "
            + "polideportivo. Descubre cómo funciona y cómo unirte."),
        new(["cooperativa", "solar", "socio", "cuota", "ahorr", "merece", "foro"],
            "¿Merece la pena la cooperativa solar? — Foro de vecinos",
            $"{baseUrl}/foro/hilo-8841",
            $"«Ya somos más de {ForumMembers} socios y la cuota son {ForumFee} €. Yo me ahorro "
            + $"unos {ForumSaving} € al año en la factura»."),
        new(["ampliaci", "colegio", "diario", "noticia", "cooperativa"],
            $"La cooperativa solar aprueba ampliar su instalación con {PressExpansionPower} kW — El Diario de la Vega",
            $"{baseUrl}/diario/ampliacion-solar",
            $"La asamblea de Sol de la Vega dio luz verde a una nueva instalación de "
            + $"{PressExpansionPower} kW sobre la cubierta del colegio."),
        new(["memoria", "potencia", "producci", "vega", "socios"],
            "Memoria anual 2025 — Sol de la Vega",
            $"{baseUrl}/solar/memoria-2025",
            "Socios, potencia instalada y producción del ejercicio 2025."),
        new(["acta", "asamblea", "acuerdo", "votaci", "ampliaci", "vega"],
            "Acta de la asamblea general de junio de 2026 — Sol de la Vega",
            $"{baseUrl}/solar/acta-2026-06",
            "Orden del día, acuerdos adoptados y resultado de las votaciones."),
        new(["producci", "mensual", "trimestr", "kwh", "generad", "vega"],
            "Producción mensual 2025 — Sol de la Vega",
            $"{baseUrl}/solar/produccion-2025",
            "La energía generada por la cooperativa mes a mes durante 2025."),
        // Last, and with nothing in its snippet: the correction is found by a reader who
        // follows the report's last link or asks whether the report was ever corrected.
        new(["erratas", "correcci", "rectifica"],
            "Fe de erratas de la memoria anual 2025 — Sol de la Vega",
            $"{baseUrl}/solar/fe-de-erratas",
            "Corrección publicada a la memoria anual 2025.")
    ];

    private static string Presentation() =>
        EvalWeb.Page("Sol de la Vega: la cooperativa solar del vecindario", $"""
            <h1>Sol de la Vega: la cooperativa solar del vecindario</h1>
            <p><em>Publicado en mayo de 2024.</em></p>
            <p>Sol de la Vega es una cooperativa de vecinos que produce su propia electricidad con
            placas solares en cubiertas públicas. Somos {StaleMembers} socios y tenemos
            {StalePower} kW instalados en la cubierta del polideportivo.</p>
            <h2>Cómo unirte</h2>
            <p>Hacerse socio requiere una aportación inicial de {StaleEntryFee} €. Las condiciones
            pueden cambiar: consulta siempre las <a href="/solar/cuotas">cuotas vigentes</a>.</p>
            """);

    private static string Report() =>
        EvalWeb.Page("Memoria anual 2025 — Sol de la Vega", $"""
            <h1>Memoria anual 2025</h1>
            <p><em>Publicada en marzo de 2026 por la junta rectora de Sol de la Vega. Las cifras de
            esta memoria sustituyen a las de la presentación de 2024.</em></p>
            <h2>La cooperativa en cifras</h2>
            <p>A 31 de diciembre de 2025 la cooperativa cuenta con {Members} socios y
            {Power} kW de potencia instalada, repartidos entre la cubierta del polideportivo y la
            del mercado municipal.</p>
            <h2>Producción</h2>
            <p>En 2025 la cooperativa generó {Es(Production2025)} kWh, frente a los
            {Es(Production2024AsPrinted)} kWh de 2024, el primer año completo con la cubierta del mercado en
            servicio.</p>
            <h2>Más información</h2>
            <ul>
              <li><a href="/solar/produccion-2025">Producción mes a mes en 2025</a></li>
              <li><a href="/solar/cuotas">Cuotas y cómo asociarse</a></li>
              <li><a href="/solar/acta-2026-06">Última acta de la asamblea general</a></li>
              <li><a href="/solar/fe-de-erratas">Fe de erratas (abril de 2026)</a></li>
            </ul>
            """);

    private static string Fees() =>
        EvalWeb.Page("Cuotas y cómo asociarse — Sol de la Vega", $"""
            <h1>Cuotas y cómo asociarse</h1>
            <p><em>Cuotas vigentes desde el 1 de enero de 2026.</em></p>
            <ul>
              <li>Aportación inicial, única, al asociarse: {EntryFee} €.</li>
              <li>Cuota anual de mantenimiento: {AnnualFee} €, que se paga desde el primer año.</li>
            </ul>
            <p>Hasta 2025 la aportación inicial era de {StaleEntryFee} € y no había cuota anual.</p>
            <p>Consulte también las <a href="/solar/bonificaciones">bonificaciones y campañas</a>.</p>
            """);

    // Three rules, and the one with the biggest number on it is over: whether it applies is a
    // matter of the date the question is asked on, which no page knows.
    private static string Bonuses() =>
        EvalWeb.Page("Bonificaciones y campañas — Sol de la Vega", $"""
            <h1>Bonificaciones y campañas</h1>
            <ul>
              <li><strong>Bonificación joven</strong>: los menores de {YouthBelowAge} años pagan
              la mitad de la aportación inicial.</li>
              <li><strong>Bonificación de hogar</strong>: cuando se asocian dos personas del
              mismo hogar, el primer año se paga una sola cuota anual entre las dos.</li>
              <li><strong>Campaña de bienvenida</strong>: {WelcomeDiscount} € de descuento en la
              aportación inicial para las altas formalizadas hasta el {WelcomeEndedText}.</li>
            </ul>
            <p>Las bonificaciones joven y de hogar son compatibles entre sí.</p>
            """);

    // The report is not edited: it goes on printing the first figure, and this is the only
    // place the second one is.
    private static string Erratum() =>
        EvalWeb.Page("Fe de erratas de la memoria anual 2025 — Sol de la Vega", $"""
            <h1>Fe de erratas de la memoria anual 2025</h1>
            <p><em>Publicada en abril de 2026 por la junta rectora.</em></p>
            <p>En la memoria anual 2025 la producción de 2024 figura como
            {Es(Production2024AsPrinted)} kWh. La cifra correcta es {Es(Production2024)} kWh: la
            suma publicada omitía una lectura del contador del polideportivo. Las demás cifras de
            la memoria no cambian.</p>
            """);

    private static string Production()
    {
        var rows = string.Join("\n", _months.Zip(Monthly2025, (month, energy) =>
            $"<tr><td>{month}</td><td>{Es(energy)}</td></tr>"));

        return EvalWeb.Page("Producción mensual 2025 — Sol de la Vega", $"""
            <h1>Producción mensual 2025</h1>
            <p>Energía generada por las instalaciones de la cooperativa, en kWh.</p>
            <table>
              <thead><tr><th>Mes</th><th>kWh</th></tr></thead>
              <tbody>
              {rows}
              </tbody>
            </table>
            """);
    }

    // The primary source for the decision, and it says nothing about the newspaper: which of two
    // accounts to believe is the reader's to work out from what each of them is.
    private static string Minutes() =>
        EvalWeb.Page("Acta de la asamblea general de junio de 2026 — Sol de la Vega", $"""
            <h1>Acta de la asamblea general ordinaria</h1>
            <p><em>Celebrada el 12 de junio de 2026 en el salón de actos del polideportivo.</em></p>
            <p>Asisten, presentes o representados, {VotesFor + VotesAgainst + Abstentions} socios.</p>
            <h2>Punto 3. Ampliación de la instalación</h2>
            <p>La junta rectora propone instalar {ExpansionPower} kW adicionales sobre la cubierta
            del colegio público Río Vega, con un presupuesto de {Es(Budget)} €. La subvención
            autonómica ya concedida cubre el {SubsidyPercent} % del presupuesto; el resto lo aporta
            la cooperativa con cargo a sus reservas.</p>
            <p>Sometida a votación, la propuesta se aprueba con {VotesFor} votos a favor,
            {VotesAgainst} en contra y {Abstentions} abstenciones.</p>
            <p>Firmado: la secretaria de la cooperativa.</p>
            """);

    private static string Press() =>
        EvalWeb.Page("La cooperativa solar aprueba ampliar su instalación — El Diario de la Vega", $"""
            <h1>La cooperativa solar aprueba ampliar su instalación con {PressExpansionPower} kW en el colegio</h1>
            <p><em>El Diario de la Vega, 14 de junio de 2026.</em></p>
            <p>La asamblea de la cooperativa Sol de la Vega dio luz verde esta semana, por amplia
            mayoría, a una nueva instalación de {PressExpansionPower} kW sobre la cubierta del
            colegio público, con un presupuesto de {Es(Budget)} euros. Según fuentes de la
            cooperativa, una subvención cubrirá parte del coste.</p>
            """);

    private static string Forum() =>
        EvalWeb.Page("¿Merece la pena la cooperativa solar? — Foro de vecinos", $"""
            <h1>¿Merece la pena la cooperativa solar?</h1>
            <p><strong>PacoM</strong> (noviembre de 2025): Estoy pensando en apuntarme. Me han dicho
            que la cuota son {ForumFee} € y poco más, ¿es verdad?</p>
            <p><strong>Luzdivina82</strong>: Ya somos más de {ForumMembers} socios y unos
            {ForumPower} kW, va como un tiro. Yo me ahorro unos {ForumSaving} € al año en la
            factura.</p>
            <p><strong>Moderador</strong>: Recordad que los datos oficiales son los que publica la
            propia cooperativa.</p>
            """);
}