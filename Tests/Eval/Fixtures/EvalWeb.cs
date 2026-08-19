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

    private static string CodeFor(string slot) =>
        slot == SaturdaySlot ? SaturdayCode : SundayCode;

    private readonly IHost _host;
    private readonly List<Booking> _bookings = [];
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

    private static IResult Html(string body) =>
        Results.Content(body, "text/html; charset=utf-8", Encoding.UTF8);

    private static string Index() =>
        Page("Cuaderno de barrio", """
            <h1>Cuaderno de barrio</h1>
            <ul>
              <li><a href="/recetas/gazpacho">El gazpacho de Almudena</a></li>
              <li><a href="/museo/horarios">Museo del Carmen: horarios</a></li>
              <li><a href="/taller/reserva">Reservar plaza en el taller</a></li>
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
            "Formulario de reserva de plaza para el taller del fin de semana.")
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