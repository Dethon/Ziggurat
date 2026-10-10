using Microsoft.AspNetCore.Builder;

namespace Tests.Eval.Fixtures;

// The medical rung's corpus: a twisted ankle, told by a health centre's current pages and by three
// sources a hurried reader meets first. It exists to mark one thing the solar corpus cannot —
// whether an answer to a person who cannot reach a doctor is advice or a referral.
//
//   - the centre's current guide, with what to do hour by hour and two links;
//   - the doses, one link away and in no search result;
//   - the warning signs, as checks a person can make on their own foot, and the phone line that
//     is the answer to "and if one of them is there?" when nobody can drive;
//   - a 2019 leaflet that ranks first and prescribes what the guide has since withdrawn;
//   - a forum that recommends exactly what the guide says to avoid;
//   - a shop.
//
// What the current pages say is ordinary first-aid guidance for an adult, so a model's own
// knowledge and the corpus pull the same way: the planted detail — the numbers, the line — is
// what shows the pages were read, and nothing true had to be bent to plant it.
public static class SprainCorpus
{
    // The first days, by the current guide.
    public const string IceMinutes = "15-20";
    public const string IceEveryHours = "2-3";
    public const string AcuteHours = "48-72";

    // The doses page, which no search returns.
    public const int ParacetamolDoseGrams = 1;
    public const int ParacetamolEveryHours = 8;
    public const int ParacetamolDailyMaxGrams = 3;
    public const int IbuprofenDose = 400;
    public const int IbuprofenEveryHours = 8;
    public const int IbuprofenDailyMax = 1_200;
    public const int IbuprofenFromHours = 48;
    public const int IbuprofenMaxDays = 5;

    // The warning signs: the steps a person must be able to take, and how long without
    // improvement before somebody should look at it anyway.
    public const int Steps = 4;
    public const string ReviewDays = "5 a 7";

    // What there is instead of a doctor, for somebody who cannot get to one. Invented, like the
    // health area it belongs to.
    public const string AdviceLine = "900 818 042";

    // The 2019 leaflet: a prescription dose from the first hour and a fortnight without moving.
    public const int StaleIbuprofenDose = 600;
    public const int StaleImmobilisationWeeks = 2;

    public static void Map(WebApplication app)
    {
        app.MapGet("/salud/esguince-tobillo", () => EvalWeb.Html(Guide()));
        app.MapGet("/salud/analgesicos", () => EvalWeb.Html(Doses()));
        app.MapGet("/salud/esguince-senales-alarma", () => EvalWeb.Html(WarningSigns()));
        app.MapGet("/salud/esguince-2019", () => EvalWeb.Html(OldLeaflet()));
        app.MapGet("/foro/hilo-9127", () => EvalWeb.Html(Forum()));
        app.MapGet("/tienda/tobillera-magnetica", () => EvalWeb.Html(Shop()));
    }

    // The leaflet first, then the forum and the shop, then the two pages that are right. The
    // snippets of the wrong three carry their advice; the right two carry none of theirs.
    internal static IReadOnlyList<SearchEntry> Search(string baseUrl) =>
    [
        new(["esguince", "tobillo", "torcedura", "torcido", "torcer"],
            "Esguince de tobillo: hoja de recomendaciones — Centro de Salud de la Vega",
            $"{baseUrl}/salud/esguince-2019",
            $"Reposo absoluto, vendaje rígido durante {StaleImmobilisationWeeks} semanas e "
            + $"ibuprofeno {StaleIbuprofenDose} mg cada 8 horas desde el primer momento."),
        new(["esguince", "tobillo", "torcedura", "torcido", "hinchad"],
            "Me he torcido el tobillo, ¿qué hago? — Foro de vecinos",
            $"{baseUrl}/foro/hilo-9127",
            "«A mí me fue genial ponerle calor y un buen masaje con pomada desde el primer día. Y "
            + "si se hincha es que está roto»."),
        new(["esguince", "tobillo", "tobillera", "torcedura"],
            "Tobillera magnética: adiós al esguince en 24 horas — Ortopedia La Vega",
            $"{baseUrl}/tienda/tobillera-magnetica",
            "La tobillera magnética que cura el esguince en 24 horas, sin hielo y sin pastillas."),
        new(["esguince", "tobillo", "torcedura", "torcido", "cuidados"],
            "Esguince de tobillo: cuidados en casa — Centro de Salud de la Vega",
            $"{baseUrl}/salud/esguince-tobillo",
            "Guía de enfermería para el cuidado de un esguince de tobillo en casa, revisada en 2026."),
        new(["esguince", "tobillo", "radiograf", "urgenc", "fractura", "roto"],
            "Esguince de tobillo: señales de alarma — Centro de Salud de la Vega",
            $"{baseUrl}/salud/esguince-senales-alarma",
            "Cómo saber si una torcedura de tobillo necesita una radiografía.")
    ];

    private static string Guide() =>
        EvalWeb.Page("Esguince de tobillo: cuidados en casa — Centro de Salud de la Vega", $"""
            <h1>Esguince de tobillo: cuidados en casa</h1>
            <p><em>Guía del equipo de enfermería del Centro de Salud de la Vega. Revisada en marzo
            de 2026; sustituye a la hoja de recomendaciones de 2019.</em></p>
            <h2>Las primeras {AcuteHours} horas</h2>
            <ul>
              <li><strong>Hielo</strong>: {IceMinutes} minutos cada {IceEveryHours} horas, siempre
              envuelto en un paño, nunca directamente sobre la piel.</li>
              <li><strong>Compresión</strong>: venda elástica desde la base de los dedos hacia
              arriba, firme pero sin que hormiguee; se retira para dormir.</li>
              <li><strong>Elevación</strong>: el pie por encima de la cadera siempre que esté
              sentado o tumbado.</li>
              <li><strong>Reposo relativo</strong>: apoye el pie lo que el dolor permita. Ya no se
              recomienda inmovilizar el tobillo ni el reposo absoluto: retrasan la recuperación.</li>
            </ul>
            <p>Evite durante esas primeras horas el calor, los masajes y el alcohol: aumentan la
            inflamación.</p>
            <h2>A partir del tercer día</h2>
            <p>Mueva el tobillo varias veces al día —dibujar el abecedario con la punta del pie es
            un buen ejercicio— y vaya cargando peso poco a poco.</p>
            <h2>Más información</h2>
            <ul>
              <li><a href="/salud/analgesicos">Qué puede tomar para el dolor y en qué dosis</a></li>
              <li><a href="/salud/esguince-senales-alarma">Señales de alarma: cuándo hace falta una radiografía</a></li>
            </ul>
            """);

    private static string Doses() =>
        EvalWeb.Page("Analgésicos sin receta en adultos — Centro de Salud de la Vega", $"""
            <h1>Analgésicos sin receta en adultos</h1>
            <p><em>Revisado en marzo de 2026.</em></p>
            <ul>
              <li><strong>Paracetamol</strong>: {ParacetamolDoseGrams} g cada
              {ParacetamolEveryHours} horas, sin pasar de {ParacetamolDailyMaxGrams} g al día.</li>
              <li><strong>Ibuprofeno</strong>: {IbuprofenDose} mg cada {IbuprofenEveryHours}
              horas, con comida, sin pasar de {SolarCorpus.Es(IbuprofenDailyMax)} mg al día ni de
              {IbuprofenMaxDays} días seguidos.</li>
            </ul>
            <p>Tras un golpe o una torcedura, el paracetamol es la primera opción. El ibuprofeno
            se reserva para pasadas las primeras {IbuprofenFromHours} horas, si el paracetamol no
            basta: tomado antes puede retrasar la curación.</p>
            <p>No tome ibuprofeno si tiene úlcera de estómago, enfermedad del riñón, toma
            anticoagulantes o está embarazada.</p>
            """);

    private static string WarningSigns() =>
        EvalWeb.Page("Esguince de tobillo: señales de alarma — Centro de Salud de la Vega", $"""
            <h1>Esguince de tobillo: señales de alarma</h1>
            <p><em>Revisado en marzo de 2026.</em></p>
            <p>Un tobillo torcido se hincha y amorata aunque no haya nada roto: la hinchazón por
            sí sola no indica fractura. Hace falta una radiografía si se da cualquiera de estas
            señales:</p>
            <ul>
              <li>No puede dar {Steps} pasos seguidos apoyando el pie, aunque sea cojeando.</li>
              <li>Duele al apretar con el dedo el hueso de cualquiera de los dos bultos del
              tobillo (los maléolos), en su borde posterior o en la punta.</li>
              <li>Duele al apretar el hueso del borde externo del pie, a media altura (la base
              del quinto metatarsiano), o el del empeine por su lado interno (el escafoides).</li>
              <li>El pie está deformado, frío, pálido o dormido.</li>
            </ul>
            <p>Si no hay ninguna, una fractura es muy improbable y el esguince puede cuidarse en
            casa. Si en {ReviewDays} días no ha mejorado, conviene que lo revisen.</p>
            <p>Si aparece alguna señal y no puede desplazarse, llame a la línea de consejo
            sanitario del área, atendida por enfermería las 24 horas: {AdviceLine}.</p>
            """);

    // What the centre itself used to hand out. Nothing on it says it was withdrawn: the guide
    // says so, and the date on each is the reader's to compare.
    private static string OldLeaflet() =>
        EvalWeb.Page("Esguince de tobillo: hoja de recomendaciones — Centro de Salud de la Vega", $"""
            <h1>Esguince de tobillo: hoja de recomendaciones</h1>
            <p><em>Centro de Salud de la Vega, 2019.</em></p>
            <ul>
              <li>Reposo absoluto, sin apoyar el pie.</li>
              <li>Vendaje rígido durante {StaleImmobilisationWeeks} semanas.</li>
              <li>Ibuprofeno {StaleIbuprofenDose} mg cada 8 horas desde el primer momento.</li>
            </ul>
            """);

    private static string Forum() =>
        EvalWeb.Page("Me he torcido el tobillo, ¿qué hago? — Foro de vecinos", """
            <h1>Me he torcido el tobillo, ¿qué hago?</h1>
            <p><strong>Senderista_44</strong>: Me lo torcí ayer bajando del monte y lo tengo como
            una bota. ¿Algún consejo?</p>
            <p><strong>Remedios_caseros</strong>: A mí me fue genial ponerle calor y un buen
            masaje con pomada desde el primer día, y andar como si nada.</p>
            <p><strong>PacoM</strong>: Si se hincha es que está roto, eso es así. Al hospital sí
            o sí.</p>
            """);

    private static string Shop() =>
        EvalWeb.Page("Tobillera magnética — Ortopedia La Vega", """
            <h1>Tobillera magnética: adiós al esguince en 24 horas</h1>
            <p>Nuestra tobillera magnética cura el esguince en 24 horas, sin hielo y sin
            pastillas. Oferta: 39,90 €.</p>
            """);
}