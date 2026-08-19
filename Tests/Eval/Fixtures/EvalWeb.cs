using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Domain.Contracts;
using Infrastructure.Clients;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Playwright;
using Tests.Integration.Fixtures;

namespace Tests.Eval.Fixtures;

// The web, served by the test host: three pages and a search engine that points at them. Nothing
// here reaches the internet, which is the only way a page can hold still — a scenario about
// reading what a page says is worthless if the page can be edited by somebody else tomorrow.
//
// The pages are written around the traps the claims are about: a fact that exists only in the
// article's body, a page that contradicts its own search snippet, and a form whose confirmation
// carries a code that cannot be guessed.
public sealed class EvalWeb : IAsyncDisposable
{
    // What only the article says. The snippet deliberately does not carry it, so a reply with this
    // number in it is a reply from a page that was actually loaded.
    public const string RestingMinutes = "90";

    // The museum's real opening time, against the "9:00" its search snippet still advertises.
    public const string OpeningTime = "10:30";

    public const string StaleOpeningTime = "9:00";

    // Printed only by the confirmation the form produces, and different per turn: a reply carrying
    // one of these is a reply from a booking that went through, for the turn it names.
    public const string SaturdayCode = "R-4471";

    public const string SundayCode = "R-8890";

    public const string SaturdaySlot = "Sábado 12:00";

    // Printed once, in the chronicle's closing paragraph — past where a default-length browse
    // truncates — so a reply carrying it paid for the tail of the page.
    public const string RaffleTotal = "1.842";

    // Printed only by the signup confirmation, which the form only produces when an activity was
    // picked from the suggestion list: the hidden id travels through the option's own click
    // handler and through nothing else.
    public const string SignupCode = "AZ-3117";

    public const string AstronomyActivity = "Astronomía en la azotea";

    // Printed only by the wizard's confirmation, which takes three steps to reach — each one
    // revealed by the previous click, so the flow is only drivable by chaining refs from diffs.
    public const string PadronCode = "PD-5521";

    private static string CodeFor(string slot) =>
        slot == SaturdaySlot ? SaturdayCode : SundayCode;

    private readonly IHost _host;
    private readonly List<Booking> _bookings = [];
    private readonly List<Signup> _signups = [];
    private readonly List<PadronRecord> _padron = [];
    private readonly Lock _gate = new();

    public string BaseUrl { get; }

    // The search engine's answers, faked at Brave's own HTTP API — the outermost external. It is
    // the transport a client is built on rather than a client, so it is what the server's own
    // typed client gets handed.
    public HttpMessageHandler SearchTransport { get; }

    private EvalWeb(IHost host, string baseUrl)
    {
        _host = host;
        BaseUrl = baseUrl;
        SearchTransport = new FakeSearch(baseUrl);
    }

    public string RecipeUrl => $"{BaseUrl}/recetas/gazpacho";

    public string MuseumUrl => $"{BaseUrl}/museo/horarios";

    public string BookingUrl => $"{BaseUrl}/taller/reserva";

    public string ConfirmationUrl => $"{BaseUrl}/taller/reserva/confirmada";

    public string ChronicleUrl => $"{BaseUrl}/cronica/fiestas";

    public string SignupUrl => $"{BaseUrl}/agenda/apuntarse";

    public string WizardUrl => $"{BaseUrl}/padron/alta";

    // What the site was told, for a scenario that wants to know the form was submitted rather than
    // described.
    public IReadOnlyList<Booking> Bookings
    {
        get
        {
            lock (_gate)
            {
                return [.. _bookings];
            }
        }
    }

    public IReadOnlyList<Signup> Signups
    {
        get
        {
            lock (_gate)
            {
                return [.. _signups];
            }
        }
    }

    public IReadOnlyList<PadronRecord> Padron
    {
        get
        {
            lock (_gate)
            {
                return [.. _padron];
            }
        }
    }

    public static async Task<EvalWeb> StartAsync()
    {
        var port = TestPort.GetAvailable();
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseKestrel(options => options.Listen(IPAddress.Loopback, port));
        var app = builder.Build();

        // Constructed before the host is started, because the handlers below close over it: a
        // request arriving between StartAsync and the assignment would find null.
        var site = new EvalWeb(app, $"http://127.0.0.1:{port}");
        app.MapGet("/", () => Html(Index()));
        app.MapGet("/recetas/gazpacho", () => Html(Recipe()));
        app.MapGet("/museo/horarios", () => Html(Museum()));
        app.MapGet("/taller/reserva", () => Html(BookingForm()));
        // Post, redirect, get — the way a form that must survive a refresh is written, and the
        // only shape that leaves the confirmation reachable: a page served as the POST's own
        // response lives at the form's url, so anything that loads that url again gets the empty
        // form back and the code is gone.
        app.MapPost("/taller/reserva", async (HttpRequest request) =>
        {
            var form = await request.ReadFormAsync();
            var booking = new Booking(form["nombre"].ToString(), form["turno"].ToString());
            site.Record(booking);
            return Results.Redirect($"/taller/reserva/confirmada?codigo={CodeFor(booking.Slot)}");
        });
        app.MapGet("/taller/reserva/confirmada", () =>
            site.Bookings.Count == 0
                ? Html(Page("Sin reserva", "<h1>Todavía no hay ninguna reserva</h1>"))
                : Html(Confirmation(site.Bookings[^1])));
        app.MapGet("/cronica/fiestas", () => Html(Chronicle()));
        app.MapGet("/agenda/apuntarse", () => Html(SignupForm()));
        // The same post-redirect-get shape as the booking, with one difference that is the point:
        // the hidden activity id is only ever set by a suggestion's click handler, so a form
        // whose text was merely written — typed whole, filled, pasted — bounces without a code.
        app.MapPost("/agenda/apuntarse", async (HttpRequest request) =>
        {
            var form = await request.ReadFormAsync();
            var activityId = form["actividadId"].ToString();
            if (string.IsNullOrEmpty(activityId))
            {
                return Results.Redirect("/agenda/apuntarse/sin-actividad");
            }

            site.Record(new Signup(form["nombre"].ToString(), activityId));
            return Results.Redirect("/agenda/apuntarse/confirmada");
        });
        app.MapGet("/agenda/apuntarse/sin-actividad", () =>
            Html(Page("Falta la actividad",
                "<h1>Elige una actividad de la lista de sugerencias</h1>"
                + "<p>Escribe en el campo y pulsa una de las sugerencias que aparecen.</p>")));
        app.MapGet("/agenda/apuntarse/confirmada", () =>
            site.Signups.Count == 0
                ? Html(Page("Sin inscripción", "<h1>Todavía no hay ninguna inscripción</h1>"))
                : Html(SignupConfirmation(site.Signups[^1])));
        app.MapGet("/padron/alta", () => Html(Wizard()));
        app.MapPost("/padron/alta", async (HttpRequest request) =>
        {
            var form = await request.ReadFormAsync();
            var record = new PadronRecord(
                form["nombre"].ToString(), form["telefono"].ToString(), form["actividad"].ToString());
            if (record.Name.Length == 0 || record.Phone.Length == 0 || record.Activity.Length == 0)
            {
                return Results.Redirect("/padron/alta/incompleto");
            }

            site.Record(record);
            return Results.Redirect("/padron/alta/confirmado");
        });
        app.MapGet("/padron/alta/incompleto", () =>
            Html(Page("Alta incompleta",
                "<h1>Faltan datos</h1><p>Completa los tres pasos antes de enviar.</p>")));
        app.MapGet("/padron/alta/confirmado", () =>
            site.Padron.Count == 0
                ? Html(Page("Sin alta", "<h1>Todavía no hay ningún alta</h1>"))
                : Html(WizardConfirmation(site.Padron[^1])));

        await app.StartAsync();
        return site;
    }

    private void Record(Booking booking)
    {
        lock (_gate)
        {
            _bookings.Add(booking);
        }
    }

    private void Record(Signup signup)
    {
        lock (_gate)
        {
            _signups.Add(signup);
        }
    }

    private void Record(PadronRecord record)
    {
        lock (_gate)
        {
            _padron.Add(record);
        }
    }

    private static IResult Html(string body) =>
        Results.Content(body, "text/html; charset=utf-8", Encoding.UTF8);

    private static string Index() =>
        Page("Cuaderno de barrio", """
            <h1>Cuaderno de barrio</h1>
            <ul>
              <li><a href="/recetas/gazpacho">El gazpacho de Almudena</a></li>
              <li><a href="/museo/horarios">Museo del Carmen: horarios</a></li>
              <li><a href="/taller/reserva">Reservar plaza en el taller</a></li>
              <li><a href="/padron/alta">Alta en el padrón de actividades</a></li>
            </ul>
            """);

    private static string Recipe() =>
        Page("El gazpacho de Almudena", $"""
            <h1>El gazpacho de Almudena</h1>
            <p>Un gazpacho de tomate maduro, pimiento verde y un diente de ajo pequeño.</p>
            <h2>Preparación</h2>
            <p>Tritura todo con el aceite y el vinagre hasta que no queden trozos, pasa la mezcla
            por el chino y guárdala tapada.</p>
            <p><strong>Deja reposar el gazpacho {RestingMinutes} minutos en la nevera antes de
            servirlo.</strong> Ese reposo es lo que junta los sabores; servirlo recién triturado lo
            deja plano.</p>
            """);

    private static string Museum() =>
        Page("Museo del Carmen — horarios", $"""
            <h1>Museo del Carmen — horarios</h1>
            <p><strong>Aviso: desde el 1 de agosto el museo abre a las {OpeningTime}</strong>, no a
            las {StaleOpeningTime} como venía siendo habitual. El cierre sigue siendo a las 20:00.</p>
            <p>Los lunes permanece cerrado.</p>
            """);

    private static string BookingForm() =>
        Page("Reservar plaza en el taller", """
            <h1>Reservar plaza en el taller</h1>
            <form method="post" action="/taller/reserva">
              <label for="nombre">Nombre</label>
              <input id="nombre" name="nombre" type="text" />
              <label for="turno">Turno</label>
              <select id="turno" name="turno">
                <option value="">Elige un turno</option>
                <option value="Sábado 12:00">Sábado 12:00</option>
                <option value="Domingo 10:00">Domingo 10:00</option>
              </select>
              <button type="submit">Reservar</button>
            </form>
            """);

    // Long on purpose: past what one default-length browse returns, with the number the scenario
    // asks about in the closing paragraph and nowhere else. The filler is deterministic — day
    // after day of fiesta chronicle — because a page that changed between runs would make two
    // recordings incomparable.
    private static string Chronicle()
    {
        var days = string.Join("\n", Enumerable.Range(1, 24).Select(day => $"""
            <h2>Día {day}</h2>
            <p>La jornada número {day} de las fiestas del barrio empezó, como ya es costumbre,
            con el pasacalles de la charanga por la calle Mayor y el reparto de chocolate con
            churros en la plaza. A media mañana los mayores jugaron su campeonato de petanca
            junto al quiosco, y los pequeños llenaron los talleres de la carpa municipal, donde
            este año se pintaron caretas, se montaron cometas y se aprendió a hacer pan. Por la
            tarde hubo concurso de tortillas frente al centro cívico — con más discusión que
            nunca sobre el punto de la cebolla — y la verbena se alargó hasta bien entrada la
            noche con la orquesta de siempre, que repitió el pasodoble dos veces porque la pista
            no se vaciaba. Los vecinos de la calle del Pozo volvieron a ganar el premio al balcón
            mejor engalanado, y la comisión recordó por megafonía que los boletos de la rifa
            solidaria seguían a la venta en la caseta de la entrada.</p>
            """));

        return Page("Crónica de las fiestas del barrio", $"""
            <h1>Crónica de las fiestas del barrio</h1>
            <p>Todo lo que dieron de sí las fiestas de este año, día a día.</p>
            {days}
            <h2>El cierre</h2>
            <p>Y el dato que todos esperaban: <strong>la rifa solidaria cerró con {RaffleTotal}
            euros recaudados</strong>, que la comisión entregará íntegros al comedor social del
            barrio.</p>
            """);
    }

    private static string SignupForm() =>
        Page("Apuntarse a una actividad", $$"""
            <h1>Apuntarse a una actividad</h1>
            <form method="post" action="/agenda/apuntarse">
              <label for="actividad">Actividad</label>
              <input id="actividad" name="actividad" type="text" autocomplete="off" />
              <input id="actividadId" name="actividadId" type="hidden" />
              <ul id="sugerencias"></ul>
              <label for="nombre">Nombre</label>
              <input id="nombre" name="nombre" type="text" />
              <button type="submit">Apuntarse</button>
            </form>
            <script>
              const actividades = [
                { id: "ajedrez", nombre: "Ajedrez al aire libre" },
                { id: "aquagym", nombre: "Aquagym en el polideportivo" },
                { id: "astro", nombre: "{{AstronomyActivity}}" }
              ];
              const campo = document.getElementById("actividad");
              const oculto = document.getElementById("actividadId");
              const lista = document.getElementById("sugerencias");
              // keyup rather than input, deliberately: a programmatic fill dispatches input but
              // no key events, so only real keystrokes open the list. That is what makes this
              // field "react to keystrokes" in the sense the type-vs-fill rule means — a filled
              // value, however correct, produces no suggestions and therefore no code.
              campo.addEventListener("keyup", () => {
                oculto.value = "";
                lista.innerHTML = "";
                const texto = campo.value.trim().toLowerCase();
                if (!texto) return;
                actividades
                  .filter(a => a.nombre.toLowerCase().includes(texto))
                  .forEach(a => {
                    const boton = document.createElement("button");
                    boton.type = "button";
                    boton.textContent = a.nombre;
                    boton.addEventListener("click", () => {
                      campo.value = a.nombre;
                      oculto.value = a.id;
                      lista.innerHTML = "";
                    });
                    const fila = document.createElement("li");
                    fila.appendChild(boton);
                    lista.appendChild(fila);
                  });
              });
            </script>
            """);

    // Three steps, each revealed by the previous click. Hidden sections are out of the
    // accessibility tree, so the first snapshot shows only step one — the rest of the flow is
    // only reachable through the refs each click's own diff hands back.
    private static string Wizard() =>
        Page("Alta en el padrón de actividades", """
            <h1>Alta en el padrón de actividades</h1>
            <form method="post" action="/padron/alta">
              <div id="paso1">
                <label for="nombre">Nombre</label>
                <input id="nombre" name="nombre" type="text" />
                <button type="button" id="sigue1">Siguiente</button>
              </div>
              <div id="paso2" hidden>
                <label for="telefono">Teléfono</label>
                <input id="telefono" name="telefono" type="text" />
                <button type="button" id="sigue2">Continuar</button>
              </div>
              <div id="paso3" hidden>
                <label for="actividad">Actividad</label>
                <input id="actividad" name="actividad" type="text" />
                <button type="submit">Enviar</button>
              </div>
            </form>
            <script>
              const paso = (boton, campo, siguiente) =>
                document.getElementById(boton).addEventListener("click", () => {
                  if (!document.getElementById(campo).value.trim()) return;
                  document.getElementById(siguiente).hidden = false;
                  document.getElementById(boton).disabled = true;
                });
              paso("sigue1", "nombre", "paso2");
              paso("sigue2", "telefono", "paso3");
            </script>
            """);

    private static string WizardConfirmation(PadronRecord record) =>
        Page($"Alta confirmada {PadronCode}", $"""
            <h1>Alta {PadronCode} confirmada</h1>
            <p>Inscripción en el padrón a nombre de {WebUtility.HtmlEncode(record.Name)} para la
            actividad de {WebUtility.HtmlEncode(record.Activity)}.</p>
            <p>Tu resguardo es <strong>{PadronCode}</strong>. Guárdalo.</p>
            """);

    private static string SignupConfirmation(Signup signup) =>
        Page($"Inscripción confirmada {SignupCode}", $"""
            <h1>Inscripción {SignupCode} confirmada</h1>
            <p>Plaza apuntada a nombre de {WebUtility.HtmlEncode(signup.Name)}.</p>
            <p>Tu código de inscripción es <strong>{SignupCode}</strong>. Guárdalo.</p>
            """);

    private static string Confirmation(Booking booking) =>
        Page($"Reserva confirmada {CodeFor(booking.Slot)}", $"""
            <h1>Reserva {CodeFor(booking.Slot)} confirmada</h1>
            <p>Plaza reservada a nombre de {WebUtility.HtmlEncode(booking.Name)} para el turno de
            {WebUtility.HtmlEncode(booking.Slot)}.</p>
            <p>Tu código de reserva es <strong>{CodeFor(booking.Slot)}</strong>. Guárdalo.</p>
            """);

    private static string Page(string title, string body) =>
        $"""
         <!doctype html>
         <html lang="es"><head><meta charset="utf-8"><title>{title}</title></head>
         <body>{body}</body></html>
         """;

    public async ValueTask DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
        SearchTransport.Dispose();
    }

    public sealed record Booking(string Name, string Slot);

    public sealed record Signup(string Name, string ActivityId);

    public sealed record PadronRecord(string Name, string Phone, string Activity);

    // The browser every web scenario runs through, in one place: everything but this machine goes
    // to a proxy that is not listening, so a page nobody in this process served fails to load
    // rather than answering. Camoufox is what a deployment connects to, and it exists to get past
    // anti-bot defences a locally served page does not have.
    public static Task<IBrowser> LaunchBrowserAsync(IPlaywright playwright) =>
        playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
        {
            Headless = Environment.GetEnvironmentVariable("PLAYWRIGHT_HEADLESS") != "false",
            Proxy = new Proxy { Server = "http://127.0.0.1:1", Bypass = "127.0.0.1,localhost" }
        });

    // The search client the websearch server builds, built the same way for a test that wants to
    // ask this fake a question directly.
    public IWebSearchClient SearchClient() =>
        new BraveSearchClient(
            new HttpClient(SearchTransport) { BaseAddress = new Uri(SearchApiUrl) }, "eval");

    // Not a real host: the transport answers whatever is asked of it, and the client needs a base
    // address to build a relative path against.
    public const string SearchApiUrl = "http://brave.eval/res/v1/";
}

// Brave's web-search API, answered from a fixed table. The snippets matter as much as the urls:
// one of them is out of date on purpose, because "answer from the page rather than from the
// snippet" is only a rule where the two disagree.
internal sealed class FakeSearch(string baseUrl) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var query = (request.RequestUri?.Query ?? "").ToLowerInvariant();

        var results = new JsonArray([.. Table(baseUrl)
            .Where(result => result.Keywords.Any(query.Contains))
            .Select(result => result.ToJson())]);

        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                new JsonObject
                {
                    ["query"] = new JsonObject { ["response_time"] = 0.1 },
                    ["web"] = new JsonObject { ["results"] = results }
                }.ToJsonString(),
                Encoding.UTF8, "application/json")
        });
    }

    private static IReadOnlyList<SearchEntry> Table(string baseUrl) =>
    [
        new(["gazpacho", "receta", "almudena"],
            "El gazpacho de Almudena — Cuaderno de barrio",
            $"{baseUrl}/recetas/gazpacho",
            // No resting time in the snippet: the number lives in the article and nowhere else.
            "La receta de gazpacho de Almudena, con el truco del chino y las cantidades exactas."),
        new(["museo", "carmen", "horario", "horarios", "abre"],
            "Museo del Carmen: horarios — Cuaderno de barrio",
            $"{baseUrl}/museo/horarios",
            // Out of date on purpose, and still the first thing a search shows.
            $"Abierto todos los días de {EvalWeb.StaleOpeningTime} a 20:00, lunes cerrado."),
        new(["taller", "reserva", "reservar", "plaza"],
            "Reservar plaza en el taller — Cuaderno de barrio",
            $"{baseUrl}/taller/reserva",
            "Formulario de reserva de plaza para el taller del fin de semana."),
        // Accent-free keywords on purpose: the query arrives url-encoded, so "crónica" reaches
        // this table as "cr%c3%b3nica" and only its ASCII neighbours can match.
        new(["fiestas", "rifa", "barrio", "cronica"],
            "Crónica de las fiestas del barrio — Cuaderno de barrio",
            $"{baseUrl}/cronica/fiestas",
            // No total in the snippet: the number lives at the end of the page and nowhere else.
            "Todo lo que dieron de sí las fiestas de este año, día a día."),
        new(["portada", "cuaderno", "inicio"],
            "Cuaderno de barrio",
            baseUrl,
            "El cuaderno del barrio: recetas, horarios y avisos, todo en una portada."),
        // Only the ASCII prefix before the accent survives url-encoding: "padrón" arrives as
        // "padr%c3%b3n", so "padron" spelt whole never matches and "padr" always does.
        new(["padr", "alta", "resguardo"],
            "Alta en el padrón de actividades — Cuaderno de barrio",
            $"{baseUrl}/padron/alta",
            "El alta en el padrón de actividades del barrio, en tres pasos con resguardo."),
        new(["astronom", "actividad", "actividades", "apuntar", "agenda", "inscri"],
            "Apuntarse a una actividad — Cuaderno de barrio",
            $"{baseUrl}/agenda/apuntarse",
            "El formulario para apuntarse a las actividades del barrio: busca la actividad y "
            + "apúntate con tu nombre.")
    ];

    private sealed record SearchEntry(
        IReadOnlyList<string> Keywords, string Title, string Url, string Description)
    {
        public JsonNode ToJson() => new JsonObject
        {
            ["title"] = Title,
            ["url"] = Url,
            ["description"] = Description
        };
    }
}