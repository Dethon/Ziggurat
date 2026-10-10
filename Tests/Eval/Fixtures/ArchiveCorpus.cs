using Microsoft.AspNetCore.Builder;
using Tests.Eval.Harness;

namespace Tests.Eval.Fixtures;

// The scale rungs' corpus: a neighbourhood food bank's website, some sixty pages of it. The
// other corpora are a dozen pages a model reads whole in ten calls, which makes a rung on them a
// reading test with traps; the first model run on them cleared nearly all of it. What a small
// site cannot ask is what research mostly is — deciding what to read when not everything can
// be. So here:
//
//   - thirty-one monthly bulletins, each with its own figures and no totals;
//   - an annual report for one year and none yet for the next, so one year's total is printed
//     and the other is twelve pages added up;
//   - a programme whose start is in the bulletin of the month it began and announced nowhere
//     else by date;
//   - a notice, one of fifteen, that changed a figure the latest bulletin and the home page
//     both still give;
//   - quarterly minutes, mostly beside the point;
//   - and a search that returns a bulletin only to a query naming its year, capped at what was
//     asked for, so the archive is never one search away.
//
// Everything is generated from the formulas below, so the site is the same on every run and
// every figure a rung marks against is computed here rather than typed.
public static class ArchiveCorpus
{
    private static readonly string[] _months =
    [
        "enero", "febrero", "marzo", "abril", "mayo", "junio",
        "julio", "agosto", "septiembre", "octubre", "noviembre", "diciembre"
    ];

    // January 2024 to July 2026: the last one is the newest thing a bulletin says, three weeks
    // before the day the family is asked on.
    private const int FirstYear = 2024;
    private const int Bulletins = 31;

    private static (int Year, int Month) At(int index) => (FirstYear + index / 12, index % 12 + 1);

    // Kilos collected: a slow rise, a December campaign and an August lull. Odd-looking steps
    // on purpose, so no month is another plus a round number.
    public static readonly IReadOnlyList<int> MonthlyKilos =
    [
        .. Enumerable.Range(0, Bulletins).Select(index =>
            3_180 + 137 * index + (index * 53 % 211)
            + At(index).Month switch { 12 => 1_840, 8 => -620, _ => 0 })
    ];

    public static readonly int Kilos2024 = MonthlyKilos.Take(12).Sum();
    public static readonly int Kilos2025 = MonthlyKilos.Skip(12).Take(12).Sum();

    public static int Families(int index) => 180 + 4 * index;

    public static int Volunteers(int index) => 58 + index;

    // Home delivery: begun in the ninth bulletin, and counted in every one since.
    private const int DeliveryIndex = 8;
    public static readonly (int Year, int Month) DeliveryStarted = At(DeliveryIndex);

    private static int Delivery(int index) => 12 + 6 * (index - DeliveryIndex);

    public static readonly int DeliveryAtYearEnd = Delivery(11);

    // The volunteers, three ways: what the home page still says, what the latest bulletin says,
    // and what a notice published after it says they are now.
    public static readonly int VolunteersOnHomePage = Volunteers(6);
    public static readonly int VolunteersInLatestBulletin = Volunteers(Bulletins - 1);
    public const int VolunteersNow = 71;

    public static readonly int FamiliesAtStart = Families(0);
    public static readonly int FamiliesAtEndOf2025 = Families(23);
    public static readonly int FamiliesLatest = Families(Bulletins - 1);

    public static readonly Source ReportPage = new("/banco/memoria-2024", "Memoria 2024");
    public static readonly Source DeliveryBulletin = new("/banco/boletines/2024-09", "Boletín de septiembre de 2024");
    public static readonly Source YearEndBulletin = new("/banco/boletines/2024-12", "Boletín de diciembre de 2024");
    public static readonly Source LatestBulletin = new("/banco/boletines/2026-07", "Boletín de julio de 2026");
    public static readonly Source VolunteerNotice = new("/banco/avisos/2026-11", "Cierre del local de la calle Mayor");

    private static readonly string[] _donors =
    [
        "el mercado municipal", "la cooperativa agrícola", "dos panaderías de la avenida",
        "el colegio Río Vega", "la peña ciclista", "el supermercado de la plaza nueva",
        "la parroquia de San Blas"
    ];

    private static readonly string[] _needs =
    [
        "aceite y legumbres", "leche y cereales", "conservas de pescado", "productos de higiene",
        "potitos y pañales"
    ];

    private static readonly string[] _notes =
    [
        "El reparto se hizo en dos turnos para evitar colas a pleno sol.",
        "Se estrenaron las cámaras frigoríficas cedidas por el ayuntamiento.",
        "La recogida en los colegios funcionó mejor que la de los supermercados.",
        "Faltaron manos el segundo sábado y hubo que cerrar una hora antes.",
        "Se revisaron las fechas de caducidad de todo el almacén.",
        "La furgoneta pasó la revisión y vuelve a estar disponible todos los días.",
        "Se atendió por primera vez con cita previa, con buen resultado.",
        "Un grupo de estudiantes ayudó a ordenar el almacén durante una semana."
    ];

    private static string Slug(int index) => $"{At(index).Year}-{At(index).Month:00}";

    private static string Named(int index) => $"{_months[At(index).Month - 1]} de {At(index).Year}";

    private static string Bulletin(int index) =>
        EvalWeb.Page($"Boletín de {Named(index)} — Banco de Alimentos de la Vega", $"""
            <h1>Boletín de {Named(index)}</h1>
            <p>Kilos recogidos este mes: {SolarCorpus.Es(MonthlyKilos[index])} kg. La mayor
            aportación llegó de {_donors[index % _donors.Length]}.</p>
            <p>Familias atendidas: {Families(index)}.</p>
            <p>Voluntariado activo: {Volunteers(index)} personas.</p>
            {(index == DeliveryIndex
                ? $"<p>Este mes arranca el reparto a domicilio para personas mayores que no pueden desplazarse: {Delivery(index)} familias en la primera ruta.</p>"
                : index > DeliveryIndex
                    ? $"<p>Reparto a domicilio: {Delivery(index)} familias.</p>"
                    : "")}
            <p>Lo que más falta hace para el mes que viene: {_needs[index % _needs.Length]}.</p>
            <p>{_notes[index % _notes.Length]}</p>
            <p><em>Boletín cerrado el último día de {Named(index)}.</em></p>
            """);

    // Fifteen notices, in the order they were published and each with its date — on the board
    // and on the notice. One of them changes a figure and its title does not say so: what says
    // it is worth opening is that it is dated after the last bulletin was closed. The first
    // armed run had no dates on the board, and the model read fifteen titles about opening
    // hours and reasonably opened none.
    private static readonly (string Slug, string Date, string Title, string Body)[] _notices =
    [
        ("2025-01", "8 de enero de 2025", "Horario de invierno del almacén",
            "Hasta el 31 de marzo el almacén abre de 10:00 a 13:00, de lunes a viernes."),
        ("2025-02", "20 de febrero de 2025", "Campaña de primavera: puntos de recogida",
            "Los puntos de recogida de la campaña de primavera estarán en el mercado, el polideportivo y los dos colegios."),
        ("2025-03", "3 de marzo de 2025", "Curso de manipulación de alimentos",
            "El curso para voluntariado nuevo será los dos primeros sábados de abril."),
        ("2025-04", "17 de marzo de 2025", "Cambio de número de cuenta para donativos",
            "Desde abril los donativos se ingresan en la cuenta de la caja rural; la anterior deja de usarse."),
        ("2025-05", "7 de abril de 2025", "Recogida en los colegios",
            "La recogida de primavera en los colegios será la última semana de abril."),
        ("2025-06", "20 de junio de 2025", "Horario de verano del almacén",
            "Del 1 de julio al 31 de agosto el almacén abre de 9:00 a 12:00."),
        ("2025-07", "9 de septiembre de 2025", "Avería de la furgoneta",
            "La furgoneta estará en el taller dos semanas; las rutas se harán con vehículos particulares."),
        ("2025-08", "1 de octubre de 2025", "Asamblea anual de socios",
            "La asamblea anual se celebrará el último viernes de octubre en el centro cívico."),
        ("2026-01", "12 de enero de 2026", "Campaña de Navidad: resultados",
            "Gracias a todas las personas que participaron en la campaña de Navidad; el detalle, en el boletín de diciembre."),
        ("2026-03", "2 de marzo de 2026", "Reparto a domicilio: nuevo horario",
            "El reparto a domicilio, en marcha desde el otoño de 2024, pasa a hacerse los martes y los jueves por la mañana."),
        ("2026-05", "20 de abril de 2026", "Recogida en los colegios",
            "La recogida de primavera en los colegios será la primera semana de mayo."),
        ("2026-07", "22 de junio de 2026", "Horario de verano del almacén",
            "Del 1 de julio al 31 de agosto el almacén abre de 9:00 a 12:00."),
        ("2026-09", "6 de julio de 2026", "La memoria de 2025 se retrasa",
            "La memoria anual de 2025 se publicará en otoño; hasta entonces, los datos de 2025 están en los boletines de cada mes."),
        ("2026-11", "6 de agosto de 2026", "Cierre del local de la calle Mayor",
            $"El 5 de agosto de 2026 cerró el local de la calle Mayor y con él el turno de tarde. Tras las bajas, quedan {VolunteersNow} voluntarios activos; el boletín de julio se cerró antes del cambio."),
        ("2026-12", "12 de agosto de 2026", "Se buscan conductores",
            "Hacen falta personas con carné para las rutas de reparto de los jueves.")
    ];

    private static readonly (string Slug, string Title, string Body)[] _minutes =
    [
        ("2024-t1", "Acta de la junta del primer trimestre de 2024", "Se aprueban las cuentas de 2023 y el calendario de recogidas del año."),
        ("2024-t2", "Acta de la junta del segundo trimestre de 2024", "Se acuerda poner en marcha un piloto de reparto a domicilio después del verano, si hay voluntariado suficiente."),
        ("2024-t3", "Acta de la junta del tercer trimestre de 2024", "Se revisa el seguro de la furgoneta y se renueva el convenio con el mercado municipal."),
        ("2024-t4", "Acta de la junta del cuarto trimestre de 2024", "Se aprueba el presupuesto de 2025 y la campaña de Navidad."),
        ("2025-t1", "Acta de la junta del primer trimestre de 2025", "Se aprueban las cuentas de 2024 y la memoria anual."),
        ("2025-t2", "Acta de la junta del segundo trimestre de 2025", "Se acuerda pedir al ayuntamiento un segundo local."),
        ("2025-t3", "Acta de la junta del tercer trimestre de 2025", "Se aprueba el alquiler del local de la calle Mayor para un turno de tarde."),
        ("2025-t4", "Acta de la junta del cuarto trimestre de 2025", "Se aprueba el presupuesto de 2026.")
    ];

    private static string Listed(string heading, IEnumerable<(string Href, string Text)> links) =>
        $"<h1>{heading}</h1><ul>"
        + string.Join("", links.Select(link => $"<li><a href=\"{link.Href}\">{link.Text}</a></li>"))
        + "</ul>";

    // Every page of the site by its path: what the site serves and what the tests read.
    public static readonly IReadOnlyDictionary<string, string> Pages = new Dictionary<string, string>(
    [
        new("/banco", EvalWeb.Page("Banco de Alimentos de la Vega", $"""
            <h1>Banco de Alimentos de la Vega</h1>
            <p>Recogemos y repartimos alimentos en el barrio desde 2019. Contamos con
            {VolunteersOnHomePage} voluntarios.</p>
            <ul>
              <li><a href="/banco/boletines">Boletines mensuales</a></li>
              <li><a href="/banco/avisos">Tablón de avisos</a></li>
              <li><a href="/banco/actas">Actas de la junta</a></li>
              <li><a href="/banco/memoria-2024">Memoria 2024</a></li>
            </ul>
            """)),
        new("/banco/memoria-2024", EvalWeb.Page("Memoria 2024 — Banco de Alimentos de la Vega", $"""
            <h1>Memoria 2024</h1>
            <p><em>Aprobada por la junta en el primer trimestre de 2025.</em></p>
            <p>En 2024 se recogieron {SolarCorpus.Es(Kilos2024)} kg de alimentos. El año terminó
            con {Families(11)} familias atendidas y {Volunteers(11)} personas voluntarias.</p>
            <p>El detalle de cada mes está en los boletines.</p>
            """)),
        new("/banco/boletines", EvalWeb.Page("Boletines mensuales — Banco de Alimentos de la Vega",
            Listed("Boletines mensuales", Enumerable.Range(0, Bulletins)
                .Select(index => ($"/banco/boletines/{Slug(index)}", $"Boletín de {Named(index)}"))))),
        .. Enumerable.Range(0, Bulletins)
            .Select(index => new KeyValuePair<string, string>($"/banco/boletines/{Slug(index)}", Bulletin(index))),
        new("/banco/avisos", EvalWeb.Page("Tablón de avisos — Banco de Alimentos de la Vega",
            Listed("Tablón de avisos", _notices.Select(notice =>
                ($"/banco/avisos/{notice.Slug}", $"{notice.Date} — {notice.Title}"))))),
        .. _notices.Select(notice => new KeyValuePair<string, string>(
            $"/banco/avisos/{notice.Slug}",
            EvalWeb.Page($"{notice.Title} — Banco de Alimentos de la Vega", $"<h1>{notice.Title}</h1><p><em>Publicado el {notice.Date}.</em></p><p>{notice.Body}</p>"))),
        new("/banco/actas", EvalWeb.Page("Actas de la junta — Banco de Alimentos de la Vega",
            Listed("Actas de la junta", _minutes.Select(minute => ($"/banco/actas/{minute.Slug}", minute.Title))))),
        .. _minutes.Select(minute => new KeyValuePair<string, string>(
            $"/banco/actas/{minute.Slug}",
            EvalWeb.Page($"{minute.Title} — Banco de Alimentos de la Vega", $"<h1>{minute.Title}</h1><p>{minute.Body}</p>")))
    ]);

    public static void Map(WebApplication app) =>
        Pages.ToList().ForEach(page => app.MapGet(page.Key, () => EvalWeb.Html(page.Value)));

    // The front door, the three indexes and the one report answer to the food bank's name and
    // to nothing else, so a question about another site never meets them. A bulletin answers
    // only to a query that also names its year — never its month: a month's name is in half
    // the questions asked of the other sites — and says nothing in its snippet.
    internal static IReadOnlyList<SearchEntry> Search(string baseUrl) =>
    [
        new(["alimentos"],
            "Banco de Alimentos de la Vega",
            $"{baseUrl}/banco",
            $"Recogemos y repartimos alimentos en el barrio desde 2019. Contamos con {VolunteersOnHomePage} voluntarios."),
        new(["alimentos"],
            "Boletines mensuales — Banco de Alimentos de la Vega",
            $"{baseUrl}/banco/boletines",
            "Todos los boletines mensuales del Banco de Alimentos, desde enero de 2024."),
        new(["alimentos"],
            "Tablón de avisos — Banco de Alimentos de la Vega",
            $"{baseUrl}/banco/avisos",
            "Avisos del Banco de Alimentos para socios y voluntariado."),
        new(["alimentos"],
            "Memoria 2024 — Banco de Alimentos de la Vega",
            $"{baseUrl}/banco/memoria-2024",
            "El resumen del año 2024 aprobado por la junta."),
        new(["alimentos"],
            "Actas de la junta — Banco de Alimentos de la Vega",
            $"{baseUrl}/banco/actas",
            "Las actas trimestrales de la junta del Banco de Alimentos."),
        .. Enumerable.Range(0, Bulletins).Select(index => new SearchEntry(
            ["boletin", "bolet%c3%adn", "kilos", "alimentos"],
            $"Boletín de {Named(index)} — Banco de Alimentos de la Vega",
            $"{baseUrl}/banco/boletines/{Slug(index)}",
            "Resumen del mes: kilos recogidos, familias atendidas y voluntariado.")
        {
            Requires = [$"{At(index).Year}"]
        })
    ];
}