using System.Collections.Concurrent;
using System.Net;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Tests.Integration.Fixtures;

// The three providers a household's keys are checked with, answered in process on one port and never
// a real one: OpenRouter's key info, Idealista's token exchange and Telegram's getMe, each answering
// a key as the test said — accepted, or a status of its own — and refusing any other with 401, as a
// provider refuses a key it never issued.
public sealed class FakeKeyProviders : IAsyncDisposable
{
    private const int Stalled = 0;

    private readonly ConcurrentDictionary<string, int> _answers = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<string> _asked = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource> _held = new(StringComparer.Ordinal);
    private WebApplication _app = null!;
    private int _port;

    public string OpenRouterUrl => OpenRouterUrlOn("127.0.0.1");

    public string IdealistaUrl => RootOn("127.0.0.1");

    public string TelegramUrl => RootOn("127.0.0.1");

    // A household's own Lemonade box: its address carries its API's base.
    public string LemonadeUrl => $"{RootOn("127.0.0.1")}lemonade";

    // As a container reaches this process (the host's gateway, by the name it is given): OpenRouter's
    // API, and the root Idealista's and Telegram's are under.
    public string OpenRouterUrlOn(string host) => $"{RootOn(host)}api/v1/";

    public string RootOn(string host) => $"http://{host}:{_port}/";

    // Every key a provider was asked about, in order (Idealista's by its API key).
    public IReadOnlyList<string> Asked => [.. _asked];

    public void Accept(string key) => _answers[key] = StatusCodes.Status200OK;

    public void Answer(string key, HttpStatusCode status) => _answers[key] = (int)status;

    // Never answers this key: the request is held until the asker gives up on it.
    public void Stall(string key) => _answers[key] = Stalled;

    // Answers this key as the test said only once the returned release is called: a probe in flight
    // for as long as the test needs something else to happen meanwhile.
    public Action Hold(string key)
    {
        var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _held[key] = held;
        return () => held.TrySetResult();
    }

    // Whether the provider has been asked about this key yet (a held one is asked and still waiting).
    public bool WasAsked(string key) => _asked.Contains(key);

    // On every address when a container asks it, which reaches this process through the host's gateway.
    public static async Task<FakeKeyProviders> StartAsync(bool forContainers = false)
    {
        var fake = new FakeKeyProviders { _port = TestPort.GetAvailable() };
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseKestrel(options => options.Listen(forContainers ? IPAddress.Any : IPAddress.Loopback, fake._port));
        fake._app = builder.Build();

        // Each takes more than the context, so none is read as a bare RequestDelegate whose IResult is dropped.
        fake._app.MapGet("/api/v1/key", (HttpContext http, CancellationToken _) =>
            fake.AnswerAsync(http, http.Request.Headers.Authorization.ToString().Replace("Bearer ", "", StringComparison.Ordinal),
                """{"data":{"label":"sk-or-v1-...","usage":0}}"""));

        fake._app.MapPost("/oauth/token", (HttpContext http, CancellationToken _) =>
        {
            var basic = http.Request.Headers.Authorization.ToString().Replace("Basic ", "", StringComparison.Ordinal);
            var pair = Encoding.UTF8.GetString(Convert.FromBase64String(basic)).Split(':', 2);
            return fake.AnswerAsync(http, pair[0], """{"access_token":"t","token_type":"bearer","expires_in":43199,"scope":"read"}""");
        });

        // A Lemonade box's model listing, under /lemonade as a box's API is under its own base.
        fake._app.MapGet("/lemonade/models", (HttpContext http, CancellationToken _) =>
            fake.AnswerAsync(http, http.Request.Headers.Authorization.ToString().Replace("Bearer ", "", StringComparison.Ordinal),
                """{"data":[]}"""));

        fake._app.MapGet("/{bot}/getMe", (string bot, HttpContext http) =>
            fake.AnswerAsync(http, bot.StartsWith("bot", StringComparison.Ordinal) ? bot[3..] : "",
                """{"ok":true,"result":{"id":1,"is_bot":true,"first_name":"Test","username":"test_bot"}}"""));

        await fake._app.StartAsync();
        return fake;
    }

    public async ValueTask DisposeAsync()
    {
        await _app.DisposeAsync();
        TestPort.Release(_port);
    }

    private async Task<IResult> AnswerAsync(HttpContext http, string key, string accepted)
    {
        _asked.Enqueue(key);
        if (_held.TryGetValue(key, out var held))
        {
            await held.Task.WaitAsync(http.RequestAborted);
        }

        switch (_answers.GetValueOrDefault(key, StatusCodes.Status401Unauthorized))
        {
            case Stalled:
                await Task.Delay(Timeout.Infinite, http.RequestAborted).ContinueWith(_ => { }, CancellationToken.None);
                return Results.Empty;
            case StatusCodes.Status200OK:
                return Results.Text(accepted, "application/json");
            case var status:
                return Results.Text("""{"error":"no"}""", "application/json", statusCode: status);
        }
    }
}