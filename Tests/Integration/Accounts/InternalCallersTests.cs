using System.Net;
using System.Net.Http.Json;
using Domain.Security;
using Shouldly;
using Tests.Integration.Fixtures;

namespace Tests.Integration.Accounts;

// Each caller of the internal API asks about its own channel and nothing else: the credential a
// channel server holds is the one compose already gives its /mcp, so one leaked credential must not
// mint another channel's person. Telegram's resolves Telegram ids and redeems the bot's ways in,
// voice's resolves voice profiles and the household's guest, the chat hub's refreshes a session —
// and asking about anything else is 403, before a row is read.
public class InternalCallersTests
{
    private const string SignalR = "signalr-credential";
    private const string Telegram = "telegram-credential";
    private const string Voice = "voice-credential";

    private static Task<AccountsServer> StartAsync() =>
        AccountsServer.StartAsync(s => s with
        {
            Callers = new Dictionary<string, string>
            {
                ["mcp-channel-signalr"] = SignalR,
                ["mcp-channel-telegram"] = Telegram,
                ["mcp-channel-voice"] = Voice
            }
        });

    public static TheoryData<string, string, object> AnotherChannelsQuestions => new()
    {
        { Telegram, "/accounts/internal/resolve", new { channel = "voice", identity = "0123456789abcdef0123456789abcdef" } },
        { SignalR, "/accounts/internal/resolve", new { channel = "voice", identity = "0123456789abcdef0123456789abcdef" } },
        { Voice, "/accounts/internal/resolve", new { channel = "telegram", identity = "123456789" } },
        { SignalR, "/accounts/internal/resolve", new { channel = "telegram", identity = "123456789" } },
        { Telegram, "/accounts/internal/guest", new { channel = "voice" } },
        { SignalR, "/accounts/internal/guest", new { channel = "voice" } },
        { Voice, "/accounts/internal/telegram/web-link", new { telegramId = 123456789L } },
        { SignalR, "/accounts/internal/telegram/start", new { telegramId = 123456789L, code = "code" } },
        { Voice, "/accounts/internal/telegram/of-account", new { tenantId = "home", userId = "fran" } }
    };

    [Theory]
    [MemberData(nameof(AnotherChannelsQuestions))]
    public async Task ACallerAskingAboutAnotherChannel_Is403(string credential, string path, object body)
    {
        await using var server = await StartAsync();
        await server.MintOperatorAsync("Fran");

        var response = await PostAsync(server, path, body, credential);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    // The chat hub's person is a session's, never an account named by its id: no credential resolves
    // the chat hub's channel, so none can mint its person for whoever it likes.
    [Theory]
    [InlineData(SignalR)]
    [InlineData(Telegram)]
    [InlineData(Voice)]
    public async Task TheChatHubsChannel_IsResolvedForNobody(string credential)
    {
        await using var server = await StartAsync();
        var fran = await server.MintOperatorAsync("Fran");

        var response = await PostAsync(server, "/accounts/internal/resolve", new { channel = "signalr", identity = fran.UserId }, credential);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task EachCaller_IsAnsweredAboutItsOwnChannel()
    {
        await using var server = await StartAsync();
        var fran = await server.MintOperatorAsync("Fran");

        (await PostAsync(server, "/accounts/internal/resolve", new { channel = "voice", identity = fran.UserId }, Voice))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
        (await PostAsync(server, "/accounts/internal/guest", new { channel = "voice" }, Voice))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
        (await PostAsync(server, "/accounts/internal/resolve", new { channel = "telegram", identity = "123456789" }, Telegram))
            .StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await PostAsync(server, "/accounts/internal/telegram/group", new { chatId = -100L }, Telegram))
            .StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    // A host run hands every caller the one local secret: whoever presents it is each of them, and
    // may ask what any of them may.
    [Fact]
    public async Task ACredentialSeveralCallersShare_AsksWhatEachOfThemMay()
    {
        await using var server = await AccountsServer.StartAsync(s => s with
        {
            Callers = new Dictionary<string, string>
            {
                ["mcp-channel-signalr"] = "local-mcp-secret",
                ["mcp-channel-telegram"] = "local-mcp-secret",
                ["mcp-channel-voice"] = "local-mcp-secret"
            }
        });
        var fran = await server.MintOperatorAsync("Fran");

        (await PostAsync(server, "/accounts/internal/resolve", new { channel = "voice", identity = fran.UserId }, "local-mcp-secret"))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
        (await PostAsync(server, "/accounts/internal/resolve", new { channel = "telegram", identity = "123456789" }, "local-mcp-secret"))
            .StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    // A caller the service has no channel for — a credential added for something else — asks about
    // nothing.
    [Fact]
    public async Task ACallerWithNoChannel_Is403()
    {
        await using var server = await AccountsServer.StartAsync(s => s with
        {
            Callers = new Dictionary<string, string> { ["mcp-sugary"] = "sugary-credential" }
        });
        var fran = await server.MintOperatorAsync("Fran");

        (await PostAsync(server, "/accounts/internal/resolve", new { channel = "voice", identity = fran.UserId }, "sugary-credential"))
            .StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    private static Task<HttpResponseMessage> PostAsync(AccountsServer server, string path, object body, string credential)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body, body.GetType()) };
        request.Headers.TryAddWithoutValidation("Authorization", SharedSecret.Header(credential));
        return server.Client().SendAsync(request);
    }
}