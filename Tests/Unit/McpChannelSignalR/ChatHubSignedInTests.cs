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

// The hub knows its person from the proxy: the session check's three headers, copied onto every
// request it lets through. A connection that arrives without them did not come through the check
// and is refused before it can read a transcript, change a topic or answer an approval; one that
// arrives with them is that person, with nothing to say about who it is. Asked over a real
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
    private readonly FakeAccountResolver _accounts = new();
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
            .AddSingleton<IAccountResolver>(_accounts)
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
            .AddSingleton(new DictationSettings());

        _app = builder.Build();
        _app.MapHub<ChatHub>("/hubs/chat");
        await _app.StartAsync();
    }

    private static Dictionary<string, string> SignedIn(string userId = Fran) => new()
    {
        [ForwardedAccount.UserHeader] = userId,
        [ForwardedAccount.TenantHeader] = Home,
        [ForwardedAccount.RolesHeader] = "owner,operator"
    };

    public static TheoryData<string[]> MissingHeaders => new()
    {
        new[] { ForwardedAccount.UserHeader, ForwardedAccount.TenantHeader, ForwardedAccount.RolesHeader },
        new[] { ForwardedAccount.UserHeader },
        new[] { ForwardedAccount.TenantHeader },
        new[] { ForwardedAccount.RolesHeader }
    };

    // The handshake is answered before the hub has looked at the request, so the refusal is the
    // connection being closed under the caller, with the reason in the close.
    [Theory]
    [MemberData(nameof(MissingHeaders))]
    public async Task AConnectionWithoutTheSessionChecksHeaders_IsClosed(string[] missing)
    {
        var headers = SignedIn();
        missing.ToList().ForEach(h => headers.Remove(h));
        var connection = await ConnectAsync(headers, out var closed);

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

    // Asked once, as the connection arrives, rather than on every message.
    [Fact]
    public async Task ThePersonsName_IsAskedOncePerConnection()
    {
        _accounts.Names[Fran] = "Fran";
        _sessionService.StartSession("topic-1", "jack", 7, 42, null);
        var connection = await ConnectAsync(SignedIn(), out _);
        await ReceiveAsync();

        Send(connection, "one");
        await SingleTurnAsync();
        (await connection.InvokeAsync<bool>("EnqueueMessage", "topic-1", "two", null, null, null)).ShouldBeTrue();
        await SingleTurnAsync();

        _accounts.Asked.ShouldBe([("signalr", Fran)]);
    }

    // A name the accounts service could not give is no reason to turn a signed-in person away: the
    // headers already said who they are, so the turn goes out under their id.
    [Fact]
    public async Task WhenTheNameCannotBeResolved_TheTurnIsSentUnderTheAccountsId()
    {
        _accounts.Fails = true;
        _sessionService.StartSession("topic-1", "jack", 7, 42, null);
        var connection = await ConnectAsync(SignedIn(), out _);
        await ReceiveAsync();

        Send(connection, "hello");

        var turn = await SingleTurnAsync();
        turn.Sender.ShouldBe(Fran);
        turn.Principal.ShouldBe(new Principal("signalr", Fran, new Account(Home, Fran)));
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

    private sealed class FakeAccountResolver : IAccountResolver
    {
        public Dictionary<string, string> Names { get; } = [];
        public List<(string Channel, string Identity)> Asked { get; } = [];
        public bool Fails { get; set; }

        public Task<ResolvedAccount?> ResolveAsync(string channel, string identity, CancellationToken ct)
        {
            Asked.Add((channel, identity));
            return Fails
                ? Task.FromException<ResolvedAccount?>(new HttpRequestException("accounts unreachable"))
                : Task.FromResult(Names.TryGetValue(identity, out var name)
                    ? new ResolvedAccount(new Account(Home, identity), name)
                    : null);
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