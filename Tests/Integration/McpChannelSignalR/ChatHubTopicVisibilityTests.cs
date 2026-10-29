using Domain.Agents;
using Domain.Channels;
using Domain.Contracts;
using Domain.Conversations;
using Domain.DTOs;
using Domain.DTOs.Channel;
using Domain.DTOs.WebChat;
using Domain.Security;
using Infrastructure.Conversations;
using Infrastructure.StateManagers;
using Mcp.Hosting;
using McpChannelSignalR.Attachments;
using McpChannelSignalR.Hubs;
using McpChannelSignalR.McpTools;
using McpChannelSignalR.Modules;
using McpChannelSignalR.Services;
using McpChannelSignalR.Settings;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Shouldly;
using Tests.Integration.Fixtures;
using Tests.Unit.McpChannelSignalR;

namespace Tests.Integration.McpChannelSignalR;

// A topic is its owner's unless they share it, and an owner may grant someone every topic of the
// household. Asked as the household's people would ask it: over real connections carrying the
// headers the proxy copies on, to the hub as the server registers it, against the real topic store
// — because who a connection is, what the store pages past and what the hub refuses are three
// halves of one answer, and a call on a hub instance skips the first.
[Trait("Category", "Integration")]
public sealed class ChatHubTopicVisibilityTests(TopicSearchFixture redis)
    : IClassFixture<TopicSearchFixture>, IAsyncLifetime
{
    private const string Home = "home";
    private const string Fran = "fran";
    private const string Laura = "laura";

    // Granted "sees all conversations" by an owner.
    private const string Olga = "olga";

    // Every test its own agent, so the store the class shares never answers one test with another's.
    private readonly string _agent = $"agent-visibility-{Guid.NewGuid():N}";
    private readonly SessionService _sessions = new();
    private readonly ChannelInbox _inbox = new(new FakeTimeProvider());
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"attachments-{Guid.NewGuid():N}");
    private readonly List<HubConnection> _connections = [];
    private StreamService _streams = null!;
    private RedisThreadStateStore _store = null!;
    private WebApplication _app = null!;

    public async Task InitializeAsync()
    {
        _store = new RedisThreadStateStore(
            redis.Connection, new RetentionSettings { PurgeHorizon = TimeSpan.FromDays(365) }, TimeProvider.System);
        _streams = new StreamService(_sessions, new Mock<IPushNotificationService>().Object, NullLogger<StreamService>.Instance);
        var attachmentSettings = new AttachmentSettings { StoragePath = _root };
        var time = new FakeTimeProvider();
        var catalog = new Mock<IAgentCatalog>();
        catalog.Setup(c => c.GetAll()).Returns([new AgentCatalogEntry(_agent, "Agent", null)]);

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services
            .AddChatSignalR()
            .AddSingleton(_sessions)
            .AddSingleton(_streams)
            .AddSingleton<IHubNotificationSender, SignalRHubNotificationSender>()
            .AddSingleton<ApprovalService>()
            .AddSingleton(new ChannelNotificationEmitter(_inbox, DeliveryPolicy.Broadcast))
            .AddSingleton<IAccountResolver>(new NamedAccounts())
            .AddSingleton(catalog.Object)
            .AddSingleton<IThreadStateStore>(_store)
            .AddSingleton<IConversationFactory>(new ConversationFactory(_store, TimeProvider.System))
            .AddSingleton(new Mock<IPushSubscriptionStore>().Object)
            .AddSingleton(new AttachmentService(
                attachmentSettings,
                new AttachmentTickets(attachmentSettings, time),
                new AttachmentStore(attachmentSettings, new RetentionSettings(), new FakeConversationLiveness(), time, NullLogger<AttachmentStore>.Instance),
                NullLogger<AttachmentService>.Instance))
            .AddSingleton(new RetentionSettings())
            .AddSingleton(new DictationSettings());

        _app = builder.Build();
        _app.MapHub<ChatHub>("/hubs/chat");
        await _app.StartAsync();
    }

    public async Task DisposeAsync()
    {
        foreach (var connection in _connections)
        {
            await connection.DisposeAsync();
        }

        await _app.DisposeAsync();
        _streams.Dispose();
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public async Task ANewTopic_IsItsCreatorsAndNobodyElses()
    {
        var fran = await ConnectAsync(Fran);
        var laura = await ConnectAsync(Laura);

        var topic = await StartTopicAsync(fran, "Fran's plum jam");

        (topic.OwnerId, topic.TenantId, topic.Shared, topic.OwnerName).ShouldBe((Fran, Home, false, "Fran"));
        (await ListAsync(fran)).ShouldContain(topic.TopicId);
        (await ListAsync(laura)).ShouldNotContain(topic.TopicId);
        (await SearchAsync(fran, "plum")).ShouldContain(topic.TopicId);
        (await SearchAsync(laura, "plum")).ShouldBeEmpty();
    }

    // Naming the topic is not a way in: every call that reads it, writes it, continues it or joins
    // its stream refuses another person's private topic, and in the words it uses for one that
    // does not exist.
    [Theory]
    [MemberData(nameof(CallsByName))]
    public async Task AnotherPersonsPrivateTopic_IsRefusedByName(string call)
    {
        var fran = await ConnectAsync(Fran);
        var laura = await ConnectAsync(Laura);
        var topic = await StartTopicAsync(fran, "Fran's plum jam");

        var refusal = await Should.ThrowAsync<HubException>(() => Call(call, laura, topic));

        refusal.Message.ShouldContain("No such conversation");
        var stored = (await _store.GetTopicAsync(_agent, topic.ChatId, topic.TopicId)).ShouldNotBeNull();
        (stored.Name, stored.Shared).ShouldBe(("Fran's plum jam", false));
        _sessions.TryGetSession(topic.TopicId, out _).ShouldBeTrue();
    }

    public static TheoryData<string> CallsByName =>
    [
        "GetHistory", "MarkTopicRead", "SaveTopic", "DeleteTopic", "SetTopicShared", "CancelTopic",
        "SendMessage", "EnqueueMessage", "ResumeStream", "GetStreamState", "IsProcessing",
        "CreateUploadTicket", "GetPendingApprovalForTopic"
    ];

    private static Task Call(string call, HubConnection connection, TopicMetadata topic) => call switch
    {
        "GetHistory" => connection.InvokeAsync<IReadOnlyList<ChatHistoryMessage>>(
            "GetHistory", topic.AgentId, topic.ChatId, topic.ThreadId),
        "MarkTopicRead" => connection.InvokeAsync("MarkTopicRead", topic.AgentId, topic.ChatId, topic.TopicId),
        "SaveTopic" => connection.InvokeAsync("SaveTopic", topic with { Name = "Laura's now" }, false),
        "DeleteTopic" => connection.InvokeAsync("DeleteTopic", topic.AgentId, topic.TopicId, topic.ChatId, topic.ThreadId),
        "SetTopicShared" => connection.InvokeAsync("SetTopicShared", topic.AgentId, topic.ChatId, topic.TopicId, true),
        "CancelTopic" => connection.InvokeAsync("CancelTopic", topic.TopicId),
        "SendMessage" => ReadAllAsync(connection.StreamAsync<ChatStreamMessage>(
            "SendMessage", topic.TopicId, "let me in", null, null, null)),
        "EnqueueMessage" => connection.InvokeAsync<bool>("EnqueueMessage", topic.TopicId, "let me in", null, null, null),
        "ResumeStream" => ReadAllAsync(connection.StreamAsync<ChatStreamMessage>("ResumeStream", topic.TopicId)),
        "GetStreamState" => connection.InvokeAsync<StreamState?>("GetStreamState", topic.TopicId),
        "IsProcessing" => connection.InvokeAsync<bool>("IsProcessing", topic.TopicId),
        "CreateUploadTicket" => connection.InvokeAsync<UploadTicket>("CreateUploadTicket", topic.TopicId),
        "GetPendingApprovalForTopic" => connection.InvokeAsync<ToolApprovalRequestMessage?>(
            "GetPendingApprovalForTopic", topic.TopicId),
        _ => throw new ArgumentOutOfRangeException(nameof(call), call, null)
    };

    // Opening a conversation for sending is answered no rather than refused, the way a session the
    // server will not start always was.
    [Fact]
    public async Task AnotherPersonsPrivateTopic_CannotBeOpenedForSending()
    {
        var fran = await ConnectAsync(Fran);
        var laura = await ConnectAsync(Laura);
        var topic = await StartTopicAsync(fran, "Fran's plum jam");

        (await laura.InvokeAsync<bool>("StartSession", topic.AgentId, topic.TopicId, topic.ChatId, topic.ThreadId, "x"))
            .ShouldBeFalse();
    }

    // A conversation no topic of this chat holds — another channel's, say — is nobody's to read by
    // naming it, and is not adopted by saving a topic over it.
    [Fact]
    public async Task AConversationNoTopicHolds_IsNeitherReadNorTakenOver()
    {
        var laura = await ConnectAsync(Laura);
        await _store.AppendMessagesAsync(
            new AgentKey("123:0", _agent).ToString(),
            [new ChatMessage(ChatRole.User, "a Telegram secret")]);
        var takeover = new TopicMetadata("t-takeover", 123, 0, _agent, "mine now", DateTimeOffset.UtcNow, null);

        (await laura.InvokeAsync<IReadOnlyList<ChatHistoryMessage>>("GetHistory", _agent, 123L, 0L)).ShouldBeEmpty();
        await Should.ThrowAsync<HubException>(() => laura.InvokeAsync("SaveTopic", takeover, true));
        (await laura.InvokeAsync<bool>("StartSession", _agent, "t-takeover", 123L, 0L, "x")).ShouldBeFalse();
        (await _store.GetTopicByChatIdAndThreadIdAsync(_agent, 123, 0)).ShouldBeNull();
    }

    [Fact]
    public async Task ASharedTopic_IsTheHouseholdsToReadAndContinue_EachMessageItsSendersOwnTurn()
    {
        var fran = await ConnectAsync(Fran);
        var laura = await ConnectAsync(Laura);
        var topic = await StartTopicAsync(fran, "Fran's plum jam");

        await fran.InvokeAsync("SetTopicShared", topic.AgentId, topic.ChatId, topic.TopicId, true);

        (await ListAsync(laura)).ShouldContain(topic.TopicId);
        (await SearchAsync(laura, "plum")).ShouldContain(topic.TopicId);
        await laura.InvokeAsync<IReadOnlyList<ChatHistoryMessage>>("GetHistory", topic.AgentId, topic.ChatId, topic.ThreadId);
        (await laura.InvokeAsync<bool>("StartSession", topic.AgentId, topic.TopicId, topic.ChatId, topic.ThreadId, topic.Name))
            .ShouldBeTrue();
        await ReceiveAsync();
        _ = laura.StreamAsync<ChatStreamMessage>("SendMessage", topic.TopicId, "and some figs", null, null, null)
            .GetAsyncEnumerator().MoveNextAsync().AsTask();

        var turn = await SingleTurnAsync();
        turn.Principal.ShouldBe(new Principal("signalr", Laura, new Account(Home, Laura)));
        turn.Sender.ShouldBe("Laura");
    }

    [Fact]
    public async Task UnsharingATopic_HidesItFromTheHouseholdAgain()
    {
        var fran = await ConnectAsync(Fran);
        var laura = await ConnectAsync(Laura);
        var topic = await StartTopicAsync(fran, "Fran's plum jam");
        await fran.InvokeAsync("SetTopicShared", topic.AgentId, topic.ChatId, topic.TopicId, true);

        await fran.InvokeAsync("SetTopicShared", topic.AgentId, topic.ChatId, topic.TopicId, false);

        (await ListAsync(laura)).ShouldNotContain(topic.TopicId);
        await Should.ThrowAsync<HubException>(() => laura.InvokeAsync<IReadOnlyList<ChatHistoryMessage>>(
            "GetHistory", topic.AgentId, topic.ChatId, topic.ThreadId));
    }

    // Reading a topic is not deciding who else reads it: a member continuing a shared topic and
    // someone who sees every topic are refused the flag alike.
    [Fact]
    public async Task OnlyItsOwner_SharesOrUnsharesATopic()
    {
        var fran = await ConnectAsync(Fran);
        var laura = await ConnectAsync(Laura);
        var olga = await ConnectAsync(Olga, "sees-all");
        var shared = await StartTopicAsync(fran, "Fran's plum jam");
        var mine = await StartTopicAsync(fran, "Fran's quince paste");
        await fran.InvokeAsync("SetTopicShared", shared.AgentId, shared.ChatId, shared.TopicId, true);

        await Should.ThrowAsync<HubException>(
            () => laura.InvokeAsync("SetTopicShared", shared.AgentId, shared.ChatId, shared.TopicId, false));
        await Should.ThrowAsync<HubException>(
            () => olga.InvokeAsync("SetTopicShared", mine.AgentId, mine.ChatId, mine.TopicId, true));

        (await _store.GetTopicAsync(_agent, shared.ChatId, shared.TopicId))!.Shared.ShouldBeTrue();
        (await _store.GetTopicAsync(_agent, mine.ChatId, mine.TopicId))!.Shared.ShouldBeFalse();
    }

    [Fact]
    public async Task SomeoneWhoSeesAll_SeesEveryTopicOfTheHousehold()
    {
        var fran = await ConnectAsync(Fran);
        var olga = await ConnectAsync(Olga, "sees-all");
        var topic = await StartTopicAsync(fran, "Fran's plum jam");

        (await ListAsync(olga)).ShouldContain(topic.TopicId);
        (await SearchAsync(olga, "plum")).ShouldContain(topic.TopicId);
        await olga.InvokeAsync<IReadOnlyList<ChatHistoryMessage>>("GetHistory", topic.AgentId, topic.ChatId, topic.ThreadId);
    }

    // Reading is never deciding: the prompt is shown to whoever sees the topic, and only the person
    // whose topic it is settles it.
    [Fact]
    public async Task SomeoneWhoSeesAll_SeesAnotherPersonsApprovalButCannotAnswerIt()
    {
        var fran = await ConnectAsync(Fran);
        var olga = await ConnectAsync(Olga, "sees-all");
        var topic = await StartTopicAsync(fran, "Fran's plum jam");
        var asked = _app.Services.GetRequiredService<ApprovalService>().RequestApprovalAsync(new RequestApprovalParams
        {
            ConversationId = new ConversationIdentity(topic.ChatId, topic.ThreadId).ConversationId,
            Mode = ApprovalMode.Request,
            Requests = [new ToolApprovalRequest("msg-1", "mcp__shop__buy", new Dictionary<string, object?>())]
        });

        var shown = (await olga.InvokeAsync<ToolApprovalRequestMessage?>("GetPendingApprovalForTopic", topic.TopicId))
            .ShouldNotBeNull();
        (await olga.InvokeAsync<bool>("RespondToApprovalAsync", shown.ApprovalId, ToolApprovalResult.Approved))
            .ShouldBeFalse();
        await Eventually.Settle();
        asked.IsCompleted.ShouldBeFalse();
        (await fran.InvokeAsync<bool>("IsApprovalPending", shown.ApprovalId)).ShouldBeTrue();

        (await fran.InvokeAsync<bool>("RespondToApprovalAsync", shown.ApprovalId, ToolApprovalResult.Rejected))
            .ShouldBeTrue();
        (await asked.WaitAsync(TimeSpan.FromSeconds(10))).ShouldBe("rejected");
    }

    // A private topic's news reaches its owner and whoever sees every topic, and nobody else in the
    // space: not even the fact that it went.
    [Fact]
    public async Task APrivateTopicsChanges_ReachItsOwnerAndWhoeverSeesAll_AndNobodyElse()
    {
        var fran = await ConnectAsync(Fran);
        var laura = await ConnectAsync(Laura);
        var olga = await ConnectAsync(Olga, "sees-all");
        var heard = Listen(("fran", fran), ("laura", laura), ("olga", olga));
        var topic = await StartTopicAsync(fran, "Fran's plum jam");

        await fran.InvokeAsync("DeleteTopic", topic.AgentId, topic.TopicId, topic.ChatId, topic.ThreadId);

        await Eventually.Until(() => heard.About(topic.TopicId).Count == 2, "the owner and whoever sees all to hear");
        await Eventually.Settle();
        heard.About(topic.TopicId).Select(h => h.Who).ShouldBe(["fran", "olga"], ignoreOrder: true);
    }

    // Sharing is news for the whole space: whoever could not see the topic now can, and unsharing
    // tells them again so their list can let it go.
    [Fact]
    public async Task SharingOrUnsharing_IsToldToTheWholeSpace()
    {
        var fran = await ConnectAsync(Fran);
        var laura = await ConnectAsync(Laura);
        var heard = Listen(("laura", laura));
        var topic = await StartTopicAsync(fran, "Fran's plum jam");

        await fran.InvokeAsync("SetTopicShared", topic.AgentId, topic.ChatId, topic.TopicId, true);
        await Eventually.Until(() => heard.About(topic.TopicId).Any(h => h.Shared == true), "the share to reach the space");

        await fran.InvokeAsync("SetTopicShared", topic.AgentId, topic.ChatId, topic.TopicId, false);
        await Eventually.Until(() => heard.About(topic.TopicId).Any(h => h.Shared == false), "the unshare to reach the space");
    }

    // The review host decides whose a review copy is from the turn it shows, never from what the
    // channel says (ADR 0046): a person's turn gives a topic that person owns, and it reaches them
    // and whoever sees all alone.
    [Fact]
    public async Task APersonsReviewCopy_IsTheirsAlone()
    {
        var fran = await ConnectAsync(Fran);
        var laura = await ConnectAsync(Laura);
        var olga = await ConnectAsync(Olga, "sees-all");
        var heard = Listen(("fran", fran), ("laura", laura), ("olga", olga));

        await ShowReviewCopyAsync("31:41", new Principal("voice", Fran, new Account(Home, Fran)), "Fran");

        var topic = (await _store.GetTopicByChatIdAndThreadIdAsync(_agent, 31, 41)).ShouldNotBeNull();
        (topic.OwnerId, topic.TenantId, topic.Shared, topic.OwnerName).ShouldBe((Fran, Home, false, "Fran"));
        await Eventually.Until(() => heard.About(topic.TopicId).Count == 2, "the owner and whoever sees all to hear");
        await Eventually.Settle();
        heard.About(topic.TopicId).Select(h => h.Who).ShouldBe(["fran", "olga"], ignoreOrder: true);
        (await ListAsync(laura)).ShouldNotContain(topic.TopicId);
    }

    // A turn that acts for nobody — the voice guest's — gives a topic the household shares.
    [Fact]
    public async Task AGuestsReviewCopy_IsTheHouseholds()
    {
        var laura = await ConnectAsync(Laura);
        var heard = Listen(("laura", laura));

        await ShowReviewCopyAsync("32:42", new Principal("voice", "household"), "kitchen");

        var topic = (await _store.GetTopicByChatIdAndThreadIdAsync(_agent, 32, 42)).ShouldNotBeNull();
        (topic.OwnerId, topic.Shared).ShouldBe(((string?)null, true));
        await Eventually.Until(() => heard.About(topic.TopicId).Count > 0, "the space to hear");
        (await ListAsync(laura)).ShouldContain(topic.TopicId);
    }

    private Task<string> ShowReviewCopyAsync(string conversationId, Principal principal, string sender) =>
        CreateConversationTool.McpRun(
            _agent, "Meal estimate", sender, _app.Services,
            initialPrompt: "a plate of paella", address: null, existingConversationId: conversationId,
            principal: principal);

    private sealed record Heard(string Who, string Topic, bool? Shared);

    // What each connection was told about topics, read under the lock the handlers write under.
    private sealed class Ears
    {
        private readonly List<Heard> _heard = [];

        public void Add(Heard heard)
        {
            lock (_heard)
            {
                _heard.Add(heard);
            }
        }

        public IReadOnlyList<Heard> About(string topicId)
        {
            lock (_heard)
            {
                return _heard.Where(h => h.Topic == topicId).ToList();
            }
        }
    }

    private static Ears Listen(params (string Who, HubConnection Connection)[] listeners)
    {
        var ears = new Ears();
        foreach (var (who, connection) in listeners)
        {
            connection.On<TopicChangedNotification>("OnTopicChanged",
                n => ears.Add(new Heard(who, n.TopicId, n.Topic?.Shared)));
        }

        return ears;
    }

    // Created the way the chat client creates one: a session over the conversation its id names,
    // then the topic saved as new.
    private async Task<TopicMetadata> StartTopicAsync(HubConnection connection, string name)
    {
        var topicId = ConversationIdGenerator.NewTopicId();
        var identity = ConversationIdGenerator.CreateFor(topicId);
        (await connection.InvokeAsync<bool>("StartSession", _agent, topicId, identity.ChatId, identity.ThreadId, name))
            .ShouldBeTrue();
        await connection.InvokeAsync("SaveTopic",
            new TopicMetadata(topicId, identity.ChatId, identity.ThreadId, _agent, name, DateTimeOffset.UtcNow, null), true);
        return (await _store.GetTopicAsync(_agent, identity.ChatId, topicId)).ShouldNotBeNull();
    }

    private async Task<IReadOnlyList<string>> ListAsync(HubConnection connection) =>
        (await connection.InvokeAsync<TopicPage>("GetTopicPage", _agent, "default", null, 50, false))
        .Topics.Select(t => t.TopicId).ToList();

    private async Task<IReadOnlyList<string>> SearchAsync(HubConnection connection, string query) =>
        (await connection.InvokeAsync<TopicPage>("SearchTopics", _agent, query, "default", null, 50))
        .Topics.Select(t => t.TopicId).ToList();

    private Task<IReadOnlyList<ChannelInboxItem>> ReceiveAsync() =>
        _inbox.ReceiveAsync("channel-signalr", TimeSpan.Zero, CancellationToken.None);

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

    private async Task<HubConnection> ConnectAsync(string userId, params string[] roles)
    {
        var server = _app.GetTestServer();
        var connection = new HubConnectionBuilder()
            .WithUrl(new Uri(server.BaseAddress, "hubs/chat"), options =>
            {
                options.HttpMessageHandlerFactory = _ => server.CreateHandler();
                options.Transports = HttpTransportType.LongPolling;
                options.Headers[ForwardedAccount.UserHeader] = userId;
                options.Headers[ForwardedAccount.TenantHeader] = Home;
                options.Headers[ForwardedAccount.RolesHeader] = string.Join(',', ["member", .. roles]);
            })
            .Build();
        _connections.Add(connection);
        await connection.StartAsync();
        await connection.InvokeAsync("JoinSpace", "default");
        return connection;
    }

    // Bounded, so a stream that never ends fails here instead of waiting for a reply.
    private static async Task ReadAllAsync(IAsyncEnumerable<ChatStreamMessage> stream)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await stream.ToListAsync(timeout.Token);
    }

    // Each person goes by their id with its first letter raised.
    private sealed class NamedAccounts : IAccountResolver
    {
        public Task<ResolvedAccount?> ResolveAsync(string channel, string identity, CancellationToken ct) =>
            Task.FromResult<ResolvedAccount?>(
                new ResolvedAccount(new Account(Home, identity), char.ToUpperInvariant(identity[0]) + identity[1..]));
    }
}