using Domain.Agents;
using Domain.Channels;
using Domain.Contracts;
using Domain.DTOs;
using Domain.DTOs.Channel;
using Domain.DTOs.Household;
using Domain.DTOs.WebChat;
using Domain.Security;
using Mcp.Hosting;
using McpChannelSignalR.Hubs;
using McpChannelSignalR.Services;
using McpChannelSignalR.Settings;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Shouldly;
using Tests.Unit.McpChannelSignalR.Fixtures;

namespace Tests.Unit.McpChannelSignalR;

// A message sent while a reply runs waits in the topic's queue, held by the hub, and runs once the
// running turn ends: the hub writes it on the stream as its turn starts, vouches for it afresh and
// sends it to the agent, with no hub call in hand. The test plays the agent: it reads what the channel
// was sent and finishes each turn the way send_reply does.
public sealed class ChatHubQueueTests : IAsyncLifetime
{
    private const string TopicId = "topic-1";
    private const string ConversationId = "7:42";
    private const string Subscriber = "channel-signalr";

    private readonly SessionService _sessionService = new();
    private readonly StreamService _streamService;
    private readonly ChannelInbox _inbox = new(new FakeTimeProvider());
    private readonly ChannelNotificationEmitter _emitter;
    private readonly Mock<IThreadStateStore> _store = new();
    private readonly Mock<IHubNotificationSender> _sender = new();
    private readonly SwitchableAccounts _accounts = new();
    private readonly QueueDispatcher _dispatcher;
    private readonly ApprovalService _approvals;
    private readonly MutableAgentCatalog _catalog = new();
    private readonly HouseholdSpacesAccounts _households = new();
    private readonly Lock _receivedLock = new();
    private readonly List<ChannelMessageNotification> _received = [];

    public ChatHubQueueTests()
    {
        _streamService = new StreamService(
            _sessionService, _store.Object, new Mock<IPushNotificationService>().Object,
            NullLogger<StreamService>.Instance);
        _emitter = new ChannelNotificationEmitter(_inbox, DeliveryPolicy.Broadcast);
        _dispatcher = new QueueDispatcher(
            _streamService, _emitter, _accounts, _store.Object, _sender.Object,
            NullLogger<QueueDispatcher>.Instance);
        _approvals = new ApprovalService(
            _streamService, _sessionService, _store.Object, _sender.Object,
            new Mock<IPushNotificationService>().Object, new AccountConnections(), NullLogger<ApprovalService>.Instance);

        // Jack reads pictures, so a message carrying one is queued rather than refused.
        _catalog.Replace([
            new AgentCatalogEntry("jack", "Jack", null, DefaultModel: "sees/pictures",
                DefaultModelAttachmentKinds: [AttachmentKind.Image])
        ]);
        _households.SavedAgents["home"] = new AgentsSection
        {
            Agents = [new HouseholdAgent { Id = "jack", Definition = new AgentDefinition
            {
                Id = "jack", Name = "Jack", Model = "sees/pictures", McpServerEndpoints = []
            } }]
        };
        _sessionService.StartSession(TopicId, "jack", 7, 42, "default", "Chat");
    }

    public async Task InitializeAsync()
    {
        await _inbox.ReceiveAsync(Subscriber, TimeSpan.Zero, CancellationToken.None);
        await _dispatcher.StartAsync(CancellationToken.None);
    }

    public async Task DisposeAsync()
    {
        await _dispatcher.StopAsync(CancellationToken.None);
        _dispatcher.Dispose();
        _streamService.Dispose();
    }

    private ChatHub HubFor(HubCallerContext caller, string displayName = "Fran")
    {
        var hub = new ChatHub(
            _sessionService, _streamService, _approvals, _emitter, _catalog,
            _store.Object, pushSubscriptionStore: null!, attachmentService: null!, _sender.Object,
            new RetentionSettings(), new DictationSettings(), _accounts, _households,
            NullLogger<ChatHub>.Instance)
        {
            Context = caller
        };
        hub.Context.Items[ChatHub.DisplayNameItem] = displayName;
        return hub;
    }

    [Fact]
    public async Task TwoMessagesQueuedBehindARunningReply_RunOneAfterTheOther_EachWrittenOnlyAsItsTurnStarts()
    {
        var hub = HubFor(new SignedInCaller());
        var wire = Read(hub.SendMessage(TopicId, "first", "c-1", null, null, CancellationToken.None));
        await AgentWasSentAsync("first");

        (await hub.EnqueueMessage(TopicId, "second", "c-2", null)).ShouldBeTrue();
        (await hub.EnqueueMessage(TopicId, "third", "c-3", null)).ShouldBeTrue();
        await Eventually.Settle();
        Sent().ShouldBe(["first"]);
        wire.Said().ShouldBe(["user: first"]);

        await FinishTurnAsync("answer one");
        await AgentWasSentAsync("second");
        await FinishTurnAsync("answer two");
        await AgentWasSentAsync("third");
        await FinishTurnAsync("answer three");

        await wire.Ended;
        wire.Said().ShouldBe([
            "user: first", "agent: answer one",
            "user: second", "agent: answer two",
            "user: third", "agent: answer three"
        ]);
        _streamService.IsStreaming(TopicId).ShouldBeFalse();
    }

    [Fact]
    public async Task ASessionSignedOutWhileItsMessageWaits_IsRefusedAtDispatch_AndTheNextQueuedMessageStillRuns()
    {
        var fran = HubFor(new SignedInCaller());
        var marta = HubFor(CallerAs("marta", "session-2"), "Marta");
        var wire = Read(fran.SendMessage(TopicId, "first", "c-1", null, null, CancellationToken.None));
        await AgentWasSentAsync("first");
        (await marta.EnqueueMessage(TopicId, "from marta", "c-2", null)).ShouldBeTrue();
        (await fran.EnqueueMessage(TopicId, "from fran", "c-3", null)).ShouldBeTrue();

        _accounts.SignOut("session-2");
        await FinishTurnAsync("answer one");
        await AgentWasSentAsync("from fran");
        await FinishTurnAsync("answer two");

        await wire.Ended;
        Sent().ShouldBe(["first", "from fran"]);
        var said = wire.Said();
        said.Count.ShouldBe(6);
        said.Take(3).ShouldBe(["user: first", "agent: answer one", "user: from marta"]);
        said[3].ShouldStartWith("error:");
        said[3].ShouldContain("signed out");
        said.Skip(4).ShouldBe(["user: from fran", "agent: answer two"]);
    }

    [Fact]
    public async Task AQueuedMessage_RunsAfterItsSendersConnectionHasClosed()
    {
        var fran = HubFor(new SignedInCaller());
        var closing = new ClosingCaller("marta", "session-2");
        var marta = HubFor(closing, "Marta");
        var wire = Read(fran.SendMessage(TopicId, "first", "c-1", null, null, CancellationToken.None));
        await AgentWasSentAsync("first");
        (await marta.EnqueueMessage(TopicId, "from marta", "c-2", null)).ShouldBeTrue();

        await closing.CloseAsync();
        await FinishTurnAsync("answer one");

        await AgentWasSentAsync("from marta");
        await FinishTurnAsync("answer two");
        await wire.Ended;
        wire.Said().ShouldContain("user: from marta");
    }

    // A page that had not heard the stream start sends as onto an idle topic: its message must not be
    // sent ahead of the queue, and its wire follows the stream from the start.
    [Fact]
    public async Task ASendRacingARunningStream_IsQueued_NotSentAheadOfTheQueue()
    {
        var fran = HubFor(new SignedInCaller());
        var marta = HubFor(CallerAs("marta", "session-2"), "Marta");
        var franWire = Read(fran.SendMessage(TopicId, "first", "c-1", null, null, CancellationToken.None));
        await AgentWasSentAsync("first");
        (await fran.EnqueueMessage(TopicId, "queued", "c-2", null)).ShouldBeTrue();

        var martaWire = Read(marta.SendMessage(TopicId, "racing", "c-3", null, null, CancellationToken.None));
        await Eventually.Until(() => martaWire.Said().Contains("user: first"), "marta's wire is handed what the stream carried");
        await Eventually.Settle();
        Sent().ShouldBe(["first"]);

        await FinishTurnAsync("answer one");
        await AgentWasSentAsync("queued");
        await FinishTurnAsync("answer two");
        await AgentWasSentAsync("racing");
        await FinishTurnAsync("answer three");

        await Task.WhenAll(franWire.Ended, martaWire.Ended);
        Sent().ShouldBe(["first", "queued", "racing"]);
        martaWire.Said().ShouldBe(franWire.Said());
    }

    // Each push carries the whole queue: the last one a page heard is what it shows, and a push that
    // overtook a later one is told apart by its revision.
    [Fact]
    public async Task TheQueue_IsToldToThePrivateTopicsAudience_InTheOrderItWillRun()
    {
        GivenTheTopic(owner: "fran", shared: false);
        var told = TellsOfTheQueue();
        var fran = HubFor(new SignedInCaller());
        var wire = Read(fran.SendMessage(TopicId, "first", "c-1", null, null, CancellationToken.None));
        await AgentWasSentAsync("first");

        (await fran.EnqueueMessage(TopicId, "second", "c-2", null, [Photo])).ShouldBeTrue();
        (await fran.EnqueueMessage(TopicId, "third", "c-3", null)).ShouldBeTrue();

        Latest(told).Items.Select(i => (i.Content, i.FileCount, i.SenderName)).ShouldBe(
            [("second", 1, "Fran"), ("third", 0, "Fran")]);
        told.Select(t => t.Group).Distinct().Order().ShouldBe(
            ["space:home:default:sees-all", "space:home:default:user:fran"]);

        await FinishTurnAsync("answer one");
        await AgentWasSentAsync("second");
        await Eventually.Until(() => Latest(told).Items.Count == 1, "the queue is told its head left");
        Latest(told).Items.ShouldHaveSingleItem().Content.ShouldBe("third");

        await FinishTurnAsync("answer two");
        await AgentWasSentAsync("third");
        await Eventually.Until(() => Latest(told).Items.Count == 0, "the queue is told its last message left");
        await FinishTurnAsync("answer three");
        await wire.Ended;
    }

    // A message refused as it is queued is told to its sender; the turn running is no part of it.
    [Fact]
    public async Task AMessageRefusedAsItIsQueued_IsAnsweredToItsSender_AndLeavesTheRunningTurnAlone()
    {
        var fran = HubFor(new SignedInCaller());
        var wire = Read(fran.SendMessage(TopicId, "first", "c-1", null, null, CancellationToken.None));
        await AgentWasSentAsync("first");

        _accounts.SignOut("session-1");
        var refused = await Should.ThrowAsync<Microsoft.AspNetCore.SignalR.HubException>(
            () => fran.EnqueueMessage(TopicId, "second", "c-2", null));

        refused.Message.ShouldContain("signed out");
        _streamService.IsStreaming(TopicId).ShouldBeTrue();
        await FinishTurnAsync("answer one");
        await wire.Ended;
        wire.Said().ShouldBe(["user: first", "agent: answer one"]);
        Sent().ShouldBe(["first"]);
    }

    // The running turn may end while a message is still being admitted to its queue: the place the
    // message held keeps the stream open, and the message runs next.
    [Fact]
    public async Task ATurnEndingWhileAMessageIsBeingQueued_LeavesTheStreamOpen_AndTheMessageRunsNext()
    {
        var fran = HubFor(new SignedInCaller());
        var wire = Read(fran.SendMessage(TopicId, "first", "c-1", null, null, CancellationToken.None));
        await AgentWasSentAsync("first");

        var vouching = _accounts.HoldNext();
        var queueing = fran.EnqueueMessage(TopicId, "second", "c-2", null);
        await vouching.Asked;
        await FinishTurnAsync("answer one");
        _streamService.IsStreaming(TopicId).ShouldBeTrue();

        vouching.Answer();
        (await queueing).ShouldBeTrue();
        await AgentWasSentAsync("second");
        await FinishTurnAsync("answer two");
        await wire.Ended;
        wire.Said().ShouldBe(["user: first", "agent: answer one", "user: second", "agent: answer two"]);
    }

    // A stop while a message is being admitted takes the place it held with the stream it was held on:
    // a stream another send opened meanwhile is no part of it, and the page is told there was nothing
    // to queue behind.
    [Fact]
    public async Task AStopWhileAMessageIsBeingQueued_LeavesTheNextStreamAlone()
    {
        var fran = HubFor(new SignedInCaller());
        var marta = HubFor(CallerAs("marta", "session-2"), "Marta");
        var stopped = Read(fran.SendMessage(TopicId, "first", "c-1", null, null, CancellationToken.None));
        await AgentWasSentAsync("first");

        var vouching = _accounts.HoldNext();
        var queueing = fran.EnqueueMessage(TopicId, "second", "c-2", null);
        await vouching.Asked;
        await fran.CancelTopic(TopicId);
        await stopped.Ended;
        var next = Read(marta.SendMessage(TopicId, "new", "c-3", null, null, CancellationToken.None));
        await AgentWasSentAsync("new");

        vouching.Answer();
        (await queueing).ShouldBeFalse();
        await FinishTurnAsync("answer new");
        await next.Ended;
        await Eventually.Settle();

        next.Said().ShouldBe(["user: new", "agent: answer new"]);
        Sent().ShouldBe(["first", "new"]);
    }

    // Two people queueing at once: each one's messages run in the order they sent them, and all of
    // them in the order the queue showed.
    [Fact]
    public async Task ConcurrentEnqueuesFromTwoConnections_RunInTheOrderTheQueueShowed()
    {
        var fran = HubFor(new SignedInCaller());
        var marta = HubFor(CallerAs("marta", "session-2"), "Marta");
        var wire = Read(fran.SendMessage(TopicId, "first", "c-1", null, null, CancellationToken.None));
        await AgentWasSentAsync("first");

        async Task QueueFiveAsync(ChatHub hub, string who)
        {
            foreach (var n in Enumerable.Range(1, 5))
            {
                (await hub.EnqueueMessage(TopicId, $"{who} {n}", null, null)).ShouldBeTrue();
            }
        }

        await Task.WhenAll(Task.Run(() => QueueFiveAsync(fran, "fran")), Task.Run(() => QueueFiveAsync(marta, "marta")));
        var shown = (await fran.GetQueue(TopicId)).Select(item => item.Content).ToList();

        foreach (var next in shown)
        {
            await FinishTurnAsync($"answer before {next}");
            await AgentWasSentAsync(next);
        }

        await FinishTurnAsync("last answer");
        await wire.Ended;
        Sent().ShouldBe(["first", .. shown]);
        shown.Where(s => s.StartsWith("fran")).ShouldBe(Enumerable.Range(1, 5).Select(n => $"fran {n}"));
        shown.Where(s => s.StartsWith("marta")).ShouldBe(Enumerable.Range(1, 5).Select(n => $"marta {n}"));
    }

    [Fact]
    public async Task Stopping_EmptiesTheQueue_SoNothingQueuedIsSentAfterwards()
    {
        var fran = HubFor(new SignedInCaller());
        var wire = Read(fran.SendMessage(TopicId, "first", "c-1", null, null, CancellationToken.None));
        await AgentWasSentAsync("first");
        (await fran.EnqueueMessage(TopicId, "second", "c-2", null)).ShouldBeTrue();

        await fran.CancelTopic(TopicId);
        await wire.Ended;
        await FinishTurnAsync("answer one, too late");
        await Eventually.Settle();

        Sent().ShouldBe(["first"]);
        (await fran.GetQueue(TopicId)).ShouldBeEmpty();
    }

    [Fact]
    public async Task ARemovedMessage_LeavesTheQueue_AndNeverRuns()
    {
        var fran = HubFor(new SignedInCaller());
        var wire = Read(fran.SendMessage(TopicId, "first", "c-1", null, null, CancellationToken.None));
        await AgentWasSentAsync("first");
        (await fran.EnqueueMessage(TopicId, "second", "c-2", null)).ShouldBeTrue();
        (await fran.EnqueueMessage(TopicId, "third", "c-3", null)).ShouldBeTrue();
        var second = (await fran.GetQueue(TopicId)).First(item => item.Content == "second");

        await fran.RemoveQueued(TopicId, second.Id);

        (await fran.GetQueue(TopicId)).Select(item => item.Content).ShouldBe(["third"]);
        await FinishTurnAsync("answer one");
        await AgentWasSentAsync("third");
        await FinishTurnAsync("answer three");
        await wire.Ended;
        Sent().ShouldBe(["first", "third"]);
        wire.Said().ShouldNotContain("user: second");
    }

    private static readonly AttachmentReference Photo = new()
    {
        Id = "7-42/abc",
        FileName = "photo.png",
        MediaType = "image/png",
        SizeBytes = 4
    };

    private void GivenTheTopic(string owner, bool shared) =>
        _store.Setup(s => s.GetTopicByChatIdAndThreadIdAsync("jack", 7, 42))
            .ReturnsAsync(new TopicMetadata(TopicId, 7, 42, "jack", "Chat", DateTimeOffset.UtcNow, null,
                OwnerId: owner, TenantId: "home", Shared: shared));

    private List<(string Group, QueueChangedNotification Notification)> TellsOfTheQueue()
    {
        var told = new List<(string, QueueChangedNotification)>();
        _sender.Setup(s => s.SendToGroupAsync(
                It.IsAny<string>(), "OnQueueChanged", It.IsAny<object>(), It.IsAny<CancellationToken>()))
            .Callback((string group, string _, object notification, CancellationToken _) =>
            {
                lock (told)
                {
                    told.Add((group, (QueueChangedNotification)notification));
                }
            })
            .Returns(Task.CompletedTask);
        return told;
    }

    private static QueueChangedNotification Latest(List<(string Group, QueueChangedNotification Notification)> told)
    {
        lock (told)
        {
            return told.Select(t => t.Notification).MaxBy(n => n.Revision)!;
        }
    }

    private static SignedInCaller CallerAs(string userId, string sessionId)
    {
        var caller = new SignedInCaller();
        caller.Items[ChatHub.AccountItem] = new ForwardedAccount(userId, "home", ["member"], SessionId: sessionId);
        return caller;
    }

    private IReadOnlyList<string> Sent()
    {
        lock (_receivedLock)
        {
            return [.. _received.Select(m => m.Content)];
        }
    }

    private async Task AgentWasSentAsync(string content) =>
        await Eventually.Until(async () =>
        {
            var items = await _inbox.ReceiveAsync(Subscriber, TimeSpan.Zero, CancellationToken.None);
            lock (_receivedLock)
            {
                _received.AddRange(items.Select(i => i.Message).OfType<ChannelMessageNotification>());
                return _received.Any(m => m.Content == content);
            }
        }, $"the agent is sent \"{content}\"");

    // As send_reply finishes a turn: its last words, marked complete.
    private Task FinishTurnAsync(string answer) =>
        _streamService.WriteReplyAsync(new SendReplyParams
        {
            ConversationId = ConversationId,
            Content = answer,
            ContentType = ReplyContentType.Text,
            IsComplete = true,
            MessageId = answer
        });

    private static Wire Read(IAsyncEnumerable<ChatStreamMessage> chunks) => new(chunks);

    // One page's wire on the topic's stream, read in the background, as a browser reads it.
    private sealed class Wire
    {
        private readonly Lock _lock = new();
        private readonly List<ChatStreamMessage> _chunks = [];

        public Wire(IAsyncEnumerable<ChatStreamMessage> chunks) => Ended = ReadAsync(chunks);

        public Task Ended { get; }

        public IReadOnlyList<ChatStreamMessage> Chunks()
        {
            lock (_lock)
            {
                return [.. _chunks];
            }
        }

        // What a reader of the wire was shown, in order: each person's message and each answer's words.
        public IReadOnlyList<string> Said() =>
        [
            .. Chunks()
                .Where(c => c.UserMessage is not null || !string.IsNullOrEmpty(c.Content) || c.Error is not null)
                .Select(c => c.UserMessage is not null ? $"user: {c.Content}"
                    : c.Error is not null ? $"error: {c.Error}"
                    : $"agent: {c.Content}")
        ];

        private async Task ReadAsync(IAsyncEnumerable<ChatStreamMessage> chunks)
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            await foreach (var chunk in chunks.WithCancellation(cts.Token))
            {
                lock (_lock)
                {
                    _chunks.Add(chunk);
                }
            }
        }
    }

    // A connection that closes once its message is queued, as a tab closed after sending does.
    private sealed class ClosingCaller(string userId, string sessionId) : HubCallerContext
    {
        private readonly CancellationTokenSource _closed = new();

        public override string ConnectionId => $"conn-{userId}";
        public override string? UserIdentifier => userId;
        public override System.Security.Claims.ClaimsPrincipal? User => null;
        public override IDictionary<object, object?> Items { get; } = new Dictionary<object, object?>
        {
            [ChatHub.AccountItem] = new ForwardedAccount(userId, "home", ["member"], SessionId: sessionId)
        };
        public override IFeatureCollection Features { get; } = new FeatureCollection();
        public override CancellationToken ConnectionAborted => _closed.Token;

        public Task CloseAsync() => _closed.CancelAsync();

        public override void Abort() => _closed.Cancel();
    }

    // The accounts service as a queued message meets it: live unless its session was signed out
    // meanwhile, and, when a test holds it, answering only once the test says so.
    private sealed class SwitchableAccounts : ISessionAccounts
    {
        private readonly Lock _lock = new();
        private readonly HashSet<string> _signedOut = [];
        private HeldAnswer? _held;

        public void SignOut(string sessionId)
        {
            lock (_lock)
            {
                _signedOut.Add(sessionId);
            }
        }

        public HeldAnswer HoldNext()
        {
            lock (_lock)
            {
                return _held = new HeldAnswer();
            }
        }

        public async Task<ResolvedAccount?> SessionAsync(string userId, string sessionId, CancellationToken ct)
        {
            HeldAnswer? held;
            lock (_lock)
            {
                held = _held;
                _held = null;
            }

            if (held is not null)
            {
                await held.WaitAsync();
            }

            lock (_lock)
            {
                var account = new Account("home", userId);
                return _signedOut.Contains(sessionId)
                    ? null
                    : new ResolvedAccount(account, userId, TestAssertions.Of(account, ChannelIds.SignalR, sessionId: sessionId));
            }
        }
    }

    private sealed class HeldAnswer
    {
        private readonly TaskCompletionSource _asked = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _answer = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Asked => _asked.Task;

        public void Answer() => _answer.TrySetResult();

        public Task WaitAsync()
        {
            _asked.TrySetResult();
            return _answer.Task;
        }
    }
}