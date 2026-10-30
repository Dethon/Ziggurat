using Domain.Channels;
using Domain.Contracts;
using Domain.DTOs;
using Domain.DTOs.Channel;
using Domain.DTOs.WebChat;
using Domain.Security;
using Mcp.Hosting;
using McpChannelSignalR.Attachments;
using McpChannelSignalR.Hubs;
using McpChannelSignalR.Modules;
using McpChannelSignalR.Services;
using McpChannelSignalR.Settings;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Shouldly;

namespace Tests.Unit.McpChannelSignalR;

// The hub knows its person from the proxy: the assertion the session check signs, copied onto every
// request it lets through and verified against the accounts service's keys — never the three plain
// headers beside it. A connection that arrives without a believable one did not come through the
// check and is refused before it can read a transcript, change a topic or answer an approval; one
// that arrives with one is that person, with nothing to say about who it is. Asked over a real
// connection to a hub registered the way the server registers it, because both the registration
// and the refusal are the hub pipeline's and a call on a hub instance skips them.
public sealed class ChatHubSignedInTests : IAsyncLifetime
{
    private const string Fran = "0123456789abcdef0123456789abcdef";
    private const string Home = "fedcba9876543210fedcba9876543210";

    private readonly SessionService _sessionService = new();
    private readonly StreamService _streamService;
    private readonly Mock<IThreadStateStore> _store = new();
    private readonly Mock<IPushSubscriptionStore> _pushStore = new();
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"attachments-{Guid.NewGuid():N}");
    private readonly List<HubConnection> _connections = [];
    private readonly ChannelInbox _inbox = new(new FakeTimeProvider());
    private readonly FakeSessionAccounts _accounts = new();
    private WebApplication _app = null!;

    public ChatHubSignedInTests()
    {
        _streamService = new StreamService(
            _sessionService,
            new Mock<IThreadStateStore>().Object,
            new Mock<IPushNotificationService>().Object,
            NullLogger<StreamService>.Instance);
    }

    public async Task InitializeAsync()
    {
        var attachmentSettings = new AttachmentSettings { StoragePath = _root };
        var time = new FakeTimeProvider();

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services
            .AddChatSignalR()
            .AddSingleton(_sessionService)
            .AddSingleton(_streamService)
            .AddSingleton(new ApprovalService(
            _streamService,
            _sessionService,
            new Mock<IThreadStateStore>().Object,
                new Mock<IHubNotificationSender>().Object,
                new Mock<IPushNotificationService>().Object,
                new AccountConnections(),
                NullLogger<ApprovalService>.Instance))
            .AddSingleton(new ChannelNotificationEmitter(_inbox, DeliveryPolicy.Broadcast))
            .AddSingleton<ISessionAccounts>(_accounts)
            .AddSingleton(new Mock<IAgentCatalog>().Object)
            .AddSingleton(_store.Object)
            .AddSingleton(_pushStore.Object)
            .AddSingleton(new AttachmentService(
                attachmentSettings,
                new AttachmentTickets(attachmentSettings, time),
                new AttachmentStore(attachmentSettings, new RetentionSettings(), new FakeConversationLiveness(), time, NullLogger<AttachmentStore>.Instance),
                NullLogger<AttachmentService>.Instance))
            .AddSingleton(new Mock<IHubNotificationSender>().Object)
            .AddSingleton(new RetentionSettings())
            .AddSingleton(new DictationSettings())
            .AddSingleton(TestAssertions.Verifier());

        _app = builder.Build();
        _app.MapHub<ChatHub>("/hubs/chat");
        await _app.StartAsync();
    }

    private const string Somebody = "91eb477ecd34251c52fd4493523a0334";
    private const string Phone = "5e55105e55105e55105e55105e55105e";

    // The session check's four headers as the proxy copies them: the plain three naming whoever a
    // test likes, and the assertion, signed with the accounts service's test key, naming the person
    // and the session it checked.
    private static Dictionary<string, string> SignedIn(string userId = Fran, string? plainUserId = null) =>
        Forwarded(plainUserId ?? userId,
            TestAssertions.Of(new Account(Home, userId), ChannelIds.SignalR, ["owner", "operator"], sessionId: Phone));

    private static Dictionary<string, string> Forwarded(string plainUserId, string? assertion)
    {
        var headers = new Dictionary<string, string>
        {
            [ForwardedAccount.UserHeader] = plainUserId,
            [ForwardedAccount.TenantHeader] = Home,
            [ForwardedAccount.RolesHeader] = "owner,operator"
        };
        if (assertion is not null)
        {
            headers[ForwardedAccount.AssertionHeader] = assertion;
        }

        return headers;
    }

    // No assertion beside the plain headers, which anything on the compose network could write; one
    // signed by a key the accounts service does not publish; one signed for another app's route; and
    // the household's guest, who is nobody to the chat client.
    public static TheoryData<string, string?> Unbelieved => new()
    {
        { "none", null },
        { "forged", TestAssertions.Forged(new Account(Home, Fran), ChannelIds.SignalR) },
        { "for sugary", TestAssertions.Of(new Account(Home, Fran), "sugary") },
        { "the guest", TestAssertions.OfGuest(Home, ChannelIds.SignalR) }
    };

    // The handshake is answered before the hub has looked at the request, so the refusal is the
    // connection being closed under the caller, with the reason in the close.
    [Theory]
    [MemberData(nameof(Unbelieved))]
    public async Task AConnectionWithoutABelievableAssertion_IsClosed(string _, string? assertion)
    {
        var connection = await ConnectAsync(Forwarded(Fran, assertion), out var closed);

        var reason = await closed.WaitAsync(TimeSpan.FromSeconds(10));

        reason.ShouldNotBeNull().Message.ShouldContain("not signed in");
        await Should.ThrowAsync<Exception>(
            () => connection.InvokeAsync<IReadOnlyList<ChatHistoryMessage>>("GetHistory", "jack", 7L, 42L));
        _store.VerifyNoOtherCalls();
        _pushStore.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task AConnectionCarryingTheSessionChecksHeaders_IsAnswered()
    {
        _store.Setup(s => s.GetHistoryAsync("jack", 7, 42)).ReturnsAsync([]);
        var connection = await ConnectAsync(SignedIn(), out _);

        var history = await connection.InvokeAsync<IReadOnlyList<ChatHistoryMessage>>("GetHistory", "jack", 7L, 42L);

        history.ShouldBeEmpty();
    }

    // Who the connection is was settled when it arrived, so there is nothing left for the browser
    // to say about it: the call that used to take a name is gone.
    [Fact]
    public async Task NamingYourself_IsNotAHubCall()
    {
        var connection = await ConnectAsync(SignedIn(), out _);

        await Should.ThrowAsync<HubException>(() => connection.InvokeAsync("RegisterUser", "someone-else"));
    }

    [Fact]
    public async Task AMessage_IsSentAsTheSignedInPerson()
    {
        _sessionService.StartSession("topic-1", "jack", 7, 42, null);
        var connection = await ConnectAsync(SignedIn(), out _);

        var chunks = await ReadAllAsync(connection.StreamAsync<ChatStreamMessage>(
            "SendMessage", "topic-1", "hello", null, null, null));

        chunks.ShouldContain(c => c.UserMessage != null && c.UserMessage.SenderId == Fran);
    }

    // The turn is the person's account, named by the proxy's session check: the principal carries
    // the household and the user it vouched for, and the sender is the name the account goes by.
    [Fact]
    public async Task AMessagesTurn_ActsForTheSignedInAccountUnderItsName()
    {
        _accounts.Names[Fran] = "Fran";
        _sessionService.StartSession("topic-1", "jack", 7, 42, null);
        var connection = await ConnectAsync(SignedIn(), out _);
        await ReceiveAsync();

        Send(connection, "hello");

        var turn = await SingleTurnAsync();
        turn.Principal.ShouldBe(new Principal("signalr", Fran, new Account(Home, Fran)));
        turn.Sender.ShouldBe("Fran");
    }

    // Who the connection is was signed; plain headers naming somebody else change nothing.
    [Fact]
    public async Task AnAssertionNamingFran_UnderHeadersNamingSomebodyElse_ActsForFran()
    {
        _sessionService.StartSession("topic-1", "jack", 7, 42, null);
        var connection = await ConnectAsync(SignedIn(Fran, plainUserId: Somebody), out _);
        await ReceiveAsync();

        Send(connection, "hello");

        (await SingleTurnAsync()).Principal.ShouldBe(new Principal("signalr", Fran, new Account(Home, Fran)));
    }

    // The connection's own assertion was checked as it arrived and lives ten minutes, while a chat
    // client stays connected for hours: every turn carries one the accounts service signed for it,
    // naming the connection's person on the chat hub's channel, which is what the agent believes.
    [Fact]
    public async Task EveryTurn_CarriesAFreshAssertionOfTheConnectionsPerson()
    {
        _sessionService.StartSession("topic-1", "jack", 7, 42, null);
        var connection = await ConnectAsync(SignedIn(), out _);
        await ReceiveAsync();

        Send(connection, "one");
        var first = await SingleTurnAsync();
        (await connection.InvokeAsync<bool>("EnqueueMessage", "topic-1", "two", null, null, null)).ShouldBeTrue();
        var second = await SingleTurnAsync();

        _accounts.Asked.Distinct().ShouldBe([(Fran, Phone)]);
        first.Assertion.ShouldBe(_accounts.Issued[^2]);
        second.Assertion.ShouldBe(_accounts.Issued[^1]);
        second.Assertion.ShouldNotBe(first.Assertion);
        var verified = await TestAssertions.Verifier().VerifyTurnAsync(second.Assertion, "signalr", CancellationToken.None);
        verified.ShouldBeOfType<TurnAdmission.Admitted>().Identity.Account.ShouldBe(new Account(Home, Fran));
    }

    // A session revoked on the account page while its chat client stays connected: the connection
    // was settled as it arrived, but every turn asks for the session again, so the next one is
    // answered as signed out and the agent is sent nothing.
    [Fact]
    public async Task ATurnAfterTheConnectionsSessionWasRevoked_IsAnsweredSignedOut_AndNotSent()
    {
        _sessionService.StartSession("topic-1", "jack", 7, 42, null);
        var connection = await ConnectAsync(SignedIn(), out _);
        await ReceiveAsync();
        _accounts.Revoked.Add(Phone);

        var chunks = await ReadAllAsync(connection.StreamAsync<ChatStreamMessage>(
            "SendMessage", "topic-1", "hello", null, null, null));

        chunks.ShouldContain(c => c.Error != null && c.Error.Contains("signed out") && c.IsComplete);
        await Eventually.Settle();
        (await ReceiveAsync()).ShouldBeEmpty();
    }

    // An assertion that names no session is nobody's session to refresh: an assertion the session
    // check signed names its session, so one naming none did not come from a sign-in this connection
    // can keep being vouched for.
    [Fact]
    public async Task AConnectionWhoseAssertionNamesNoSession_StartsNoTurn()
    {
        _sessionService.StartSession("topic-1", "jack", 7, 42, null);
        var connection = await ConnectAsync(
            Forwarded(Fran, TestAssertions.Of(new Account(Home, Fran), ChannelIds.SignalR, ["owner"])), out _);
        await ReceiveAsync();

        var chunks = await ReadAllAsync(connection.StreamAsync<ChatStreamMessage>(
            "SendMessage", "topic-1", "hello", null, null, null));

        chunks.ShouldContain(c => c.Error != null && c.Error.Contains("signed out") && c.IsComplete);
        _accounts.Asked.ShouldBeEmpty();
        await Eventually.Settle();
        (await ReceiveAsync()).ShouldBeEmpty();
    }

    // A connection whose person the accounts service cannot vouch for right now still opens — a name
    // it could not give turns nobody away — but no turn leaves without the service's word: the
    // stream ends in an error, and the agent is sent nothing it would have to refuse.
    [Fact]
    public async Task WhenThePersonCannotBeVouchedFor_TheTurnIsAnsweredWithAnErrorAndNotSent()
    {
        _accounts.Fails = true;
        _sessionService.StartSession("topic-1", "jack", 7, 42, null);
        var connection = await ConnectAsync(SignedIn(), out _);
        await ReceiveAsync();

        var chunks = await ReadAllAsync(connection.StreamAsync<ChatStreamMessage>(
            "SendMessage", "topic-1", "hello", null, null, null));

        chunks.ShouldContain(c => !string.IsNullOrEmpty(c.Error) && c.IsComplete);
        await Eventually.Settle();
        (await ReceiveAsync()).ShouldBeEmpty();
    }

    [Fact]
    public async Task APushSubscription_IsKeptUnderTheSignedInPerson()
    {
        var connection = await ConnectAsync(SignedIn(), out _);
        var subscription = new PushSubscriptionDto("https://push.example/1", "key", "auth");

        await connection.InvokeAsync("SubscribePush", subscription);
        await connection.InvokeAsync("UnsubscribePush", "https://push.example/1");

        _pushStore.Verify(s => s.SaveAsync(Fran, subscription, It.IsAny<string>(), null, It.IsAny<CancellationToken>()));
        _pushStore.Verify(s => s.RemoveAsync(Fran, "https://push.example/1", It.IsAny<CancellationToken>()));
    }

    // Broadcast discards an item with no subscriber registered, so the agent's pump polls once
    // before anything is emitted for the turn to be there to read.
    private Task<IReadOnlyList<ChannelInboxItem>> ReceiveAsync() =>
        _inbox.ReceiveAsync("channel-signalr", TimeSpan.Zero, CancellationToken.None);

    // The reply stream stays open while the agent is listening, and nothing answers here, so the
    // send is started and left running for the connection's disposal to end.
    private static void Send(HubConnection connection, string message) =>
        _ = connection.StreamAsync<ChatStreamMessage>("SendMessage", "topic-1", message, null, null, null)
            .GetAsyncEnumerator().MoveNextAsync().AsTask();

    private async Task<ChannelMessageNotification> SingleTurnAsync()
    {
        List<ChannelMessageNotification> turns = [];
        await Eventually.Until(async () =>
        {
            turns.AddRange((await ReceiveAsync()).Select(i => i.Message).OfType<ChannelMessageNotification>());
            return turns.Count > 0;
        }, "the turn to reach the agent");
        return turns.ShouldHaveSingleItem();
    }

    // The accounts service as the hub asks it: every session it is asked about is its person's, under
    // the name a test gave them or their id, answered with a fresh assertion of them on the chat hub's
    // channel — each one kept, so a test can tell which answer a turn carried — until a test revokes it.
    private sealed class FakeSessionAccounts : ISessionAccounts
    {
        private readonly Lock _lock = new();
        private readonly List<(string UserId, string SessionId)> _asked = [];
        private readonly List<string> _issued = [];

        public Dictionary<string, string> Names { get; } = [];
        public HashSet<string> Revoked { get; } = [];
        public bool Fails { get; set; }

        public IReadOnlyList<(string UserId, string SessionId)> Asked
        {
            get
            {
                lock (_lock)
                {
                    return [.. _asked];
                }
            }
        }

        public IReadOnlyList<string> Issued
        {
            get
            {
                lock (_lock)
                {
                    return [.. _issued];
                }
            }
        }

        public Task<ResolvedAccount?> SessionAsync(string userId, string sessionId, CancellationToken ct)
        {
            lock (_lock)
            {
                _asked.Add((userId, sessionId));
                if (Fails)
                {
                    return Task.FromException<ResolvedAccount?>(new HttpRequestException("accounts unreachable"));
                }

                if (Revoked.Contains(sessionId))
                {
                    return Task.FromResult<ResolvedAccount?>(null);
                }

                var account = new Account(Home, userId);
                var assertion = TestAssertions.Of(account, ChannelIds.SignalR, sessionId: sessionId);
                _issued.Add(assertion);
                return Task.FromResult<ResolvedAccount?>(
                    new ResolvedAccount(account, Names.GetValueOrDefault(userId, userId), assertion));
            }
        }
    }

    private Task<HubConnection> ConnectAsync(IReadOnlyDictionary<string, string> headers, out Task<Exception?> closed)
    {
        var server = _app.GetTestServer();
        var connection = new HubConnectionBuilder()
            .WithUrl(new Uri(server.BaseAddress, "hubs/chat"), options =>
            {
                options.HttpMessageHandlerFactory = _ => server.CreateHandler();
                options.Transports = HttpTransportType.LongPolling;
                headers.ToList().ForEach(h => options.Headers[h.Key] = h.Value);
            })
            .Build();
        var closing = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.Closed += ex =>
        {
            closing.TrySetResult(ex);
            return Task.CompletedTask;
        };
        closed = closing.Task;
        _connections.Add(connection);
        return StartAsync(connection);
    }

    private static async Task<HubConnection> StartAsync(HubConnection connection)
    {
        await connection.StartAsync();
        return connection;
    }

    // Bounded, so a stream that never ends fails here instead of waiting for a reply.
    private static async Task<List<ChatStreamMessage>> ReadAllAsync(IAsyncEnumerable<ChatStreamMessage> stream)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        return await stream.ToListAsync(timeout.Token);
    }

    public async Task DisposeAsync()
    {
        foreach (var connection in _connections)
        {
            await connection.DisposeAsync();
        }

        await _app.DisposeAsync();
        _streamService.Dispose();
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}