using Domain.Agents;
using Domain.Channels;
using Domain.Contracts;
using Domain.Conversations;
using Domain.DTOs;
using Domain.DTOs.Channel;
using Domain.DTOs.Household;
using Domain.DTOs.WebChat;
using Domain.Extensions;
using Domain.Security;
using Mcp.Hosting;
using McpChannelSignalR.Attachments;
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
    private readonly InboxClock _inboxClock = new();
    private readonly ChannelInbox _inbox;
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
    private readonly string _uploads = Path.Combine(Path.GetTempPath(), $"queue-uploads-{Guid.NewGuid():N}");
    private readonly AttachmentStore _attachmentStore;
    private readonly AttachmentService _attachments;

    public ChatHubQueueTests()
    {
        _streamService = new StreamService(
            _sessionService, _store.Object, new Mock<IPushNotificationService>().Object,
            NullLogger<StreamService>.Instance);
        _inbox = new ChannelInbox(_inboxClock);
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

        _store.Setup(s => s.GetConversationAsync(It.IsAny<string>()))
            .ReturnsAsync(new ConversationHistory([], new Dictionary<string, SlotVersions>()));

        var attachmentSettings = new AttachmentSettings { StoragePath = _uploads };
        var time = new FakeTimeProvider();
        _attachmentStore = new AttachmentStore(
            attachmentSettings, new RetentionSettings(), new FakeConversationLiveness(), time, NullLogger<AttachmentStore>.Instance);
        _attachments = new AttachmentService(
            attachmentSettings, new AttachmentTickets(attachmentSettings, time), _attachmentStore,
            NullLogger<AttachmentService>.Instance);
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
        if (Directory.Exists(_uploads))
        {
            Directory.Delete(_uploads, recursive: true);
        }
    }

    private ChatHub HubFor(HubCallerContext caller, string displayName = "Fran")
    {
        var hub = new ChatHub(
            _sessionService, _streamService, _approvals, _emitter, _catalog,
            _store.Object, pushSubscriptionStore: null!, _attachments, _sender.Object,
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

    // Stop empties the queue: nothing queued for a reply someone abandoned runs, and each message goes
    // back to the person who queued it, theirs alone, in the order they queued them.
    [Fact]
    public async Task Stopping_HoldsEachSendersQueuedMessagesForThem_InQueueOrder_AndRunsNothingFurther()
    {
        var fran = HubFor(new SignedInCaller());
        var marta = HubFor(CallerAs("marta", "session-2"), "Marta");
        var wire = Read(fran.SendMessage(TopicId, "first", "c-1", null, null, CancellationToken.None));
        await AgentWasSentAsync("first");
        (await marta.EnqueueMessage(TopicId, "marta one", "c-2", null)).ShouldBeTrue();
        (await fran.EnqueueMessage(TopicId, "fran one", "c-3", null, [Photo])).ShouldBeTrue();
        (await marta.EnqueueMessage(TopicId, "marta two", "c-4", null)).ShouldBeTrue();

        await fran.CancelTopic(TopicId);
        await wire.Ended;
        await FinishTurnAsync("answer one, too late");
        await Eventually.Settle();

        Sent().ShouldBe(["first"]);
        (await fran.GetQueue(TopicId)).ShouldBeEmpty();
        var frans = await fran.TakeReturned(TopicId);
        frans.Select(m => m.Content).ShouldBe(["fran one"]);
        frans[0].Attachments.ShouldBe([Photo]);
        (await marta.TakeReturned(TopicId)).Select(m => m.Content).ShouldBe(["marta one", "marta two"]);
    }

    // Each sender hears, on every page of theirs, that their messages are waiting; whichever page takes
    // them first has them, so they are never sent twice from two devices.
    [Fact]
    public async Task EachSenderIsToldTheirMessagesWait_AndATakeHandsThemOverOnce_WhicheverDeviceAsks()
    {
        var told = TellsOfReturns();
        var fran = HubFor(new SignedInCaller());
        var franOnThePhone = HubFor(CallerAs("fran", "session-phone"));
        var marta = HubFor(CallerAs("marta", "session-2"), "Marta");
        var wire = Read(fran.SendMessage(TopicId, "first", "c-1", null, null, CancellationToken.None));
        await AgentWasSentAsync("first");
        (await franOnThePhone.EnqueueMessage(TopicId, "fran one", "c-2", null)).ShouldBeTrue();
        (await marta.EnqueueMessage(TopicId, "marta one", "c-3", null)).ShouldBeTrue();

        await marta.CancelTopic(TopicId);
        await wire.Ended;

        Snapshot(told).OrderBy(t => t.Group).ShouldBe([
            ("account:fran", new QueueReturnedNotification(TopicId, "default")),
            ("account:marta", new QueueReturnedNotification(TopicId, "default"))
        ]);
        var taken = await Task.WhenAll(fran.TakeReturned(TopicId), franOnThePhone.TakeReturned(TopicId));
        taken.SelectMany(t => t).Select(m => m.Content).ShouldBe(["fran one"]);
        (await fran.TakeReturned(TopicId)).ShouldBeEmpty();
        (await marta.TakeReturned(TopicId)).Select(m => m.Content).ShouldBe(["marta one"]);
        (await marta.TakeReturned(TopicId)).ShouldBeEmpty();
    }

    // Made private after the stop, the topic is no longer the member's to read, their words in it included.
    [Fact]
    public async Task TakingReturnedMessagesOnATopicTheCallerMayNoLongerSee_IsAnsweredAsForNoSuchConversation()
    {
        GivenTheTopic(owner: "fran", shared: true);
        var fran = HubFor(new SignedInCaller());
        var marta = HubFor(CallerAs("marta", "session-2"), "Marta");
        var wire = Read(fran.SendMessage(TopicId, "first", "c-1", null, null, CancellationToken.None));
        await AgentWasSentAsync("first");
        (await marta.EnqueueMessage(TopicId, "marta one", "c-2", null)).ShouldBeTrue();
        await fran.CancelTopic(TopicId);
        await wire.Ended;

        GivenTheTopic(owner: "fran", shared: false);
        var refused = await Should.ThrowAsync<HubException>(() => marta.TakeReturned(TopicId));

        refused.Message.ShouldBe(TopicRefusals.NoSuchConversation);
        GivenTheTopic(owner: "fran", shared: true);
        (await marta.TakeReturned(TopicId)).Select(m => m.Content).ShouldBe(["marta one"]);
    }

    // An installed page from before ignores what a stop returns and never takes it: its stop still ends
    // the reply, and so does one whose pushes cannot be sent, and the topic's next message runs alone.
    [Fact]
    public async Task AStopFromAPageThatIgnoresReturns_StillStops_EvenWhenItsPushesFail()
    {
        var fran = HubFor(new SignedInCaller());
        var wire = Read(fran.SendMessage(TopicId, "first", "c-1", null, null, CancellationToken.None));
        await AgentWasSentAsync("first");
        (await fran.EnqueueMessage(TopicId, "second", "c-2", null)).ShouldBeTrue();
        _sender.Setup(s => s.SendToGroupAsync(
                It.IsAny<string>(), It.IsIn("OnQueueChanged", "OnQueueReturned"), It.IsAny<object>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException("the backplane is down"));

        await fran.CancelTopic(TopicId);
        await wire.Ended;
        await FinishTurnAsync("answer one, too late");

        var next = Read(fran.SendMessage(TopicId, "next", "c-3", null, null, CancellationToken.None));
        await AgentWasSentAsync("next");
        await FinishTurnAsync("answer next");
        await next.Ended;
        next.Said().ShouldBe(["user: next", "agent: answer next"]);
        Sent().ShouldBe(["first", "next"]);
    }

    // Taken as the turn before it ended but not yet written on the stream, a message has not started:
    // a stop then returns it to its sender like any still queued, ahead of them, and never runs it.
    [Fact]
    public async Task AStopBeforeTheNextMessagesTurnStarts_ReturnsItToo_AheadOfTheRest()
    {
        GivenTheTopic(owner: "fran", shared: true);
        var fran = HubFor(new SignedInCaller());
        var wire = Read(fran.SendMessage(TopicId, "first", "c-1", null, null, CancellationToken.None));
        await AgentWasSentAsync("first");
        (await fran.EnqueueMessage(TopicId, "second", "c-2", null)).ShouldBeTrue();
        (await fran.EnqueueMessage(TopicId, "third", "c-3", null)).ShouldBeTrue();
        var dispatching = HoldTheNextQueuePush();

        await FinishTurnAsync("answer one");
        await dispatching.Asked;
        await fran.CancelTopic(TopicId);
        dispatching.Answer();
        await wire.Ended;
        await Eventually.Settle();

        Sent().ShouldBe(["first"]);
        wire.Said().ShouldBe(["user: first", "agent: answer one"]);
        (await fran.TakeReturned(TopicId)).Select(m => m.Content).ShouldBe(["second", "third"]);
    }

    // A stop while the next message's sender is being vouched for, as its turn was about to start: it has
    // not started, so it goes back to its sender and is never drawn on the stream nor sent.
    [Fact]
    public async Task AStopWhileTheNextMessageIsBeingVouchedFor_ReturnsIt_NeitherDrawnNorSent()
    {
        var fran = HubFor(new SignedInCaller());
        var wire = Read(fran.SendMessage(TopicId, "first", "c-1", null, null, CancellationToken.None));
        await AgentWasSentAsync("first");
        (await fran.EnqueueMessage(TopicId, "second", "c-2", null)).ShouldBeTrue();
        var vouching = _accounts.HoldNext();

        await FinishTurnAsync("answer one");
        await vouching.Asked;
        await fran.CancelTopic(TopicId);
        vouching.Answer();
        await wire.Ended;
        await Eventually.Settle();

        (await SentSoFarAsync()).ShouldBe(["first"]);
        wire.Said().ShouldBe(["user: first", "agent: answer one"]);
        (await fran.TakeReturned(TopicId)).Select(m => m.Content).ShouldBe(["second"]);
    }

    // The agent may end the running turn just as the stop's cancel reaches it: by then the queue has gone
    // back to its senders, so that turn's end starts nothing after the cancel.
    [Fact]
    public async Task ATurnEndingAsTheStopReachesTheAgent_StartsNothing_AndTheWholeQueueIsReturned()
    {
        var fran = HubFor(new SignedInCaller());
        var wire = Read(fran.SendMessage(TopicId, "first", "c-1", null, null, CancellationToken.None));
        await AgentWasSentAsync("first");
        (await fran.EnqueueMessage(TopicId, "second", "c-2", null)).ShouldBeTrue();
        (await fran.EnqueueMessage(TopicId, "third", "c-3", null)).ShouldBeTrue();

        _inboxClock.WhenNextPutInTheInbox(() =>
        {
            FinishTurnAsync("answer one").GetAwaiter().GetResult();
            _inboxClock.WaitForAnotherPut(TimeSpan.FromSeconds(2));
        });
        await fran.CancelTopic(TopicId);
        await wire.Ended;
        await Eventually.Settle();

        (await SentSoFarAsync()).ShouldBe(["first"]);
        (await fran.TakeReturned(TopicId)).Select(m => m.Content).ShouldBe(["second", "third"]);
    }

    // A deleted conversation keeps nothing of what was said or queued in it, returned messages included.
    [Fact]
    public async Task DeletingTheTopic_DropsWhatAStopReturned()
    {
        GivenTheTopic(owner: "fran", shared: true);
        _store.Setup(s => s.GetTopicAsync("home", "jack", 7, TopicId))
            .ReturnsAsync(new TopicMetadata(TopicId, 7, 42, "jack", "Chat", DateTimeOffset.UtcNow, null,
                OwnerId: "fran", TenantId: "home", Shared: true));
        var fran = HubFor(new SignedInCaller());
        var marta = HubFor(CallerAs("marta", "session-2"), "Marta");
        var wire = Read(fran.SendMessage(TopicId, "first", "c-1", null, null, CancellationToken.None));
        await AgentWasSentAsync("first");
        (await marta.EnqueueMessage(TopicId, "marta one", "c-2", null)).ShouldBeTrue();
        await fran.CancelTopic(TopicId);
        await wire.Ended;

        await fran.DeleteTopic("jack", TopicId, 7, 42);

        (await marta.TakeReturned(TopicId)).ShouldBeEmpty();
    }

    // A refused turn is ended by the agent as an error and then a stream's end: one turn over, not two,
    // so the next queued message runs alone, and the one after it waits for that one's turn to end.
    [Fact]
    public async Task ATurnEndedByAnErrorThenAStreamEnd_RunsOneQueuedMessage_AndTheNextWaitsItsTurn()
    {
        var fran = HubFor(new SignedInCaller());
        var wire = Read(fran.SendMessage(TopicId, "first", "c-1", null, null, CancellationToken.None));
        await AgentWasSentAsync("first");
        (await fran.EnqueueMessage(TopicId, "second", "c-2", null)).ShouldBeTrue();
        (await fran.EnqueueMessage(TopicId, "third", "c-3", null)).ShouldBeTrue();

        await RefuseTurnAsync("refused");
        await AgentWasSentAsync("second");
        await Eventually.Settle();

        (await SentSoFarAsync()).ShouldBe(["first", "second"]);
        await FinishTurnAsync("answer two");
        await AgentWasSentAsync("third");
        await FinishTurnAsync("answer three");
        await wire.Ended;
        Sent().ShouldBe(["first", "second", "third"]);
        wire.Said().ShouldBe([
            "user: first", "error: refused",
            "user: second", "agent: answer two",
            "user: third", "agent: answer three"
        ]);
    }

    // The stream's end that follows the error is the same turn ending, so it must not end the stream
    // under the message that just started on it.
    [Fact]
    public async Task ATurnEndedByAnErrorThenAStreamEnd_KeepsTheStreamOpen_UntilTheQueuedMessagesTurnEnds()
    {
        var fran = HubFor(new SignedInCaller());
        var wire = Read(fran.SendMessage(TopicId, "first", "c-1", null, null, CancellationToken.None));
        await AgentWasSentAsync("first");
        (await fran.EnqueueMessage(TopicId, "second", "c-2", null)).ShouldBeTrue();

        await RefuseTurnAsync("refused");
        await AgentWasSentAsync("second");
        await Eventually.Settle();

        _streamService.IsStreaming(TopicId).ShouldBeTrue();
        wire.Ended.IsCompleted.ShouldBeFalse();
        await FinishTurnAsync("answer two");
        await wire.Ended;
        wire.Said().ShouldBe(["user: first", "error: refused", "user: second", "agent: answer two"]);
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

    // Everyone on a shared topic sees the queue; only the sender withdraws a message from it.
    [Fact]
    public async Task AnotherMembersRemove_IsRefused_AndTheMessageStillRuns()
    {
        var fran = HubFor(new SignedInCaller());
        var marta = HubFor(CallerAs("marta", "session-2"), "Marta");
        var wire = Read(fran.SendMessage(TopicId, "first", "c-1", null, null, CancellationToken.None));
        await AgentWasSentAsync("first");
        (await fran.EnqueueMessage(TopicId, "second", "c-2", null)).ShouldBeTrue();
        var second = (await marta.GetQueue(TopicId)).ShouldHaveSingleItem();

        var refused = await Should.ThrowAsync<HubException>(() => marta.RemoveQueued(TopicId, second.Id));

        refused.Message.ShouldBe(TopicRefusals.NotTheSenders);
        await FinishTurnAsync("answer one");
        await AgentWasSentAsync("second");
        await FinishTurnAsync("answer two");
        await wire.Ended;
    }

    // Edit: the message comes back to its sender as they queued it, to be reworked and sent again.
    [Fact]
    public async Task ATakenBackMessage_IsHandedBackAsItWasQueued_AndNeverRuns()
    {
        var fran = HubFor(new SignedInCaller());
        var wire = Read(fran.SendMessage(TopicId, "first", "c-1", null, null, CancellationToken.None));
        await AgentWasSentAsync("first");
        var patch = new AgentConfigPatch { Model = "sees/pictures", ReasoningEffort = "high" };
        (await fran.EnqueueMessage(TopicId, "second", "c-2", patch, [Photo])).ShouldBeTrue();
        var second = (await fran.GetQueue(TopicId)).ShouldHaveSingleItem();

        var taken = await fran.TakeBackQueued(TopicId, second.Id);

        (taken.Id, taken.Content, taken.ConfigPatch).ShouldBe((second.Id, "second", patch));
        taken.Attachments.ShouldBe([Photo]);
        (await fran.GetQueue(TopicId)).ShouldBeEmpty();
        await FinishTurnAsync("answer one");
        await wire.Ended;
        Sent().ShouldBe(["first"]);
    }

    [Fact]
    public async Task AnotherMembersTakeBack_IsRefused()
    {
        var fran = HubFor(new SignedInCaller());
        var marta = HubFor(CallerAs("marta", "session-2"), "Marta");
        var wire = Read(fran.SendMessage(TopicId, "first", "c-1", null, null, CancellationToken.None));
        await AgentWasSentAsync("first");
        (await fran.EnqueueMessage(TopicId, "second", "c-2", null)).ShouldBeTrue();
        var second = (await marta.GetQueue(TopicId)).ShouldHaveSingleItem();

        var refused = await Should.ThrowAsync<HubException>(() => marta.TakeBackQueued(TopicId, second.Id));

        refused.Message.ShouldBe(TopicRefusals.NotTheSenders);
        (await fran.GetQueue(TopicId)).ShouldHaveSingleItem().Id.ShouldBe(second.Id);
        await FinishTurnAsync("answer one");
        await AgentWasSentAsync("second");
        await FinishTurnAsync("answer two");
        await wire.Ended;
    }

    // The running turn ending takes the queue's head as the person withdraws it: either the withdrawal
    // wins and the message never runs, or its turn started and the person is told it was already sent.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AWithdrawalRacingTheDispatch_EitherWinsOrIsAlreadySent_NeverBoth(bool takingBack)
    {
        var fran = HubFor(new SignedInCaller());
        foreach (var round in Enumerable.Range(1, 10))
        {
            var wire = Read(fran.SendMessage(TopicId, $"first {round}", null, null, null, CancellationToken.None));
            await AgentWasSentAsync($"first {round}");
            (await fran.EnqueueMessage(TopicId, $"raced {round}", null, null)).ShouldBeTrue();
            (await fran.EnqueueMessage(TopicId, $"after {round}", null, null)).ShouldBeTrue();
            var raced = (await fran.GetQueue(TopicId))[0];

            var withdrawing = Task.Run(() => takingBack
                ? fran.TakeBackQueued(TopicId, raced.Id)
                : fran.RemoveQueued(TopicId, raced.Id));
            await Task.WhenAll(withdrawing.ContinueWith(_ => { }), Task.Run(() => FinishTurnAsync($"answer {round}")));

            if (withdrawing.IsCompletedSuccessfully)
            {
                await AgentWasSentAsync($"after {round}");
                await FinishTurnAsync($"answer after {round}");
                await wire.Ended;
                Sent().ShouldNotContain($"raced {round}");
                wire.Said().ShouldNotContain($"user: raced {round}");
            }
            else
            {
                (await Should.ThrowAsync<HubException>(() => withdrawing)).Message.ShouldBe(TopicRefusals.AlreadySent);
                await AgentWasSentAsync($"raced {round}");
                await FinishTurnAsync($"answer raced {round}");
                await AgentWasSentAsync($"after {round}");
                await FinishTurnAsync($"answer after {round}");
                await wire.Ended;
                Sent().Count(sent => sent == $"raced {round}").ShouldBe(1);
            }
        }
    }

    // A file uploaded for a message withdrawn from the queue lingers nowhere; one taken back stays,
    // since the composer it went back to holds it now.
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public async Task AWithdrawnMessagesUpload_IsDeletedWhenRemoved_AndKeptWhenTakenBack(bool takingBack, bool kept)
    {
        var fran = HubFor(new SignedInCaller());
        var wire = Read(fran.SendMessage(TopicId, "first", "c-1", null, null, CancellationToken.None));
        await AgentWasSentAsync("first");
        var photo = await UploadAsync();
        (await fran.EnqueueMessage(TopicId, "look", "c-2", null, [photo])).ShouldBeTrue();
        var queued = (await fran.GetQueue(TopicId)).ShouldHaveSingleItem();

        if (takingBack)
        {
            await fran.TakeBackQueued(TopicId, queued.Id);
        }
        else
        {
            await fran.RemoveQueued(TopicId, queued.Id);
        }

        (_attachmentStore.Find(photo.Id) is not null).ShouldBe(kept);
        await FinishTurnAsync("answer one");
        await wire.Ended;
    }

    // The same file may be named by the conversation (a message re-run with its files, which a rewrite
    // put aside) or by another queued message: removing one message leaves it to them.
    [Theory]
    [InlineData("conversation")]
    [InlineData("queue")]
    public async Task ARemovedMessagesUpload_IsKept_WhileSomethingElseHoldsIt(string holder)
    {
        var fran = HubFor(new SignedInCaller());
        var wire = Read(fran.SendMessage(TopicId, "first", "c-1", null, null, CancellationToken.None));
        await AgentWasSentAsync("first");
        var photo = await UploadAsync();
        var earlier = new Microsoft.Extensions.AI.ChatMessage(Microsoft.Extensions.AI.ChatRole.User, "look");
        earlier.SetAttachments([photo]);
        _store.Setup(s => s.GetConversationAsync(new AgentKey(ConversationId, "jack").ToString()))
            .ReturnsAsync(new ConversationHistory(holder == "conversation" ? [earlier] : [], new Dictionary<string, SlotVersions>()));
        (await fran.EnqueueMessage(TopicId, "look", "c-2", null, [photo])).ShouldBeTrue();
        if (holder == "queue")
        {
            (await fran.EnqueueMessage(TopicId, "look again", "c-3", null, [photo])).ShouldBeTrue();
        }

        await fran.RemoveQueued(TopicId, (await fran.GetQueue(TopicId))[0].Id);

        _attachmentStore.Find(photo.Id).ShouldNotBeNull();
        await FinishTurnAsync("answer one");
        if (holder == "queue")
        {
            await AgentWasSentAsync("look again");
            await FinishTurnAsync("answer two");
        }

        await wire.Ended;
    }

    // A page names its files by id, and nothing checked the id was this conversation's when the message
    // was queued: removing it deletes nothing another conversation keeps.
    [Fact]
    public async Task ARemovedMessageNamingAnotherConversationsFile_LeavesThatFileAlone()
    {
        var fran = HubFor(new SignedInCaller());
        var wire = Read(fran.SendMessage(TopicId, "first", "c-1", null, null, CancellationToken.None));
        await AgentWasSentAsync("first");
        var theirs = await _attachmentStore.SaveAsync(
            new AgentKey("9:99", "jack"), "default", "photo.png", "image/png", new MemoryStream([1, 2, 3]),
            CancellationToken.None);
        (await fran.EnqueueMessage(TopicId, "look", "c-2", null, [theirs])).ShouldBeTrue();

        await fran.RemoveQueued(TopicId, (await fran.GetQueue(TopicId)).ShouldHaveSingleItem().Id);

        _attachmentStore.Find(theirs.Id).ShouldNotBeNull();
        await FinishTurnAsync("answer one");
        await wire.Ended;
    }

    // Naming a private topic's queued message tells someone who may not see the topic nothing about it.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AWithdrawalOnATopicTheCallerMayNotSee_IsAnsweredAsForNoSuchConversation(bool takingBack)
    {
        GivenTheTopic(owner: "fran", shared: false);
        var fran = HubFor(new SignedInCaller());
        var marta = HubFor(CallerAs("marta", "session-2"), "Marta");
        var wire = Read(fran.SendMessage(TopicId, "first", "c-1", null, null, CancellationToken.None));
        await AgentWasSentAsync("first");
        (await fran.EnqueueMessage(TopicId, "second", "c-2", null)).ShouldBeTrue();
        var second = (await fran.GetQueue(TopicId)).ShouldHaveSingleItem();

        var refused = await Should.ThrowAsync<HubException>(() => takingBack
            ? marta.TakeBackQueued(TopicId, second.Id)
            : marta.RemoveQueued(TopicId, second.Id));

        refused.Message.ShouldBe(TopicRefusals.NoSuchConversation);
        await FinishTurnAsync("answer one");
        await AgentWasSentAsync("second");
        await FinishTurnAsync("answer two");
        await wire.Ended;
    }

    // The sender's other devices and everyone else on the topic lose the chip too.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AWithdrawal_IsToldToTheTopicsAudience(bool takingBack)
    {
        GivenTheTopic(owner: "fran", shared: true);
        var told = TellsOfTheQueue();
        var fran = HubFor(new SignedInCaller());
        var wire = Read(fran.SendMessage(TopicId, "first", "c-1", null, null, CancellationToken.None));
        await AgentWasSentAsync("first");
        (await fran.EnqueueMessage(TopicId, "second", "c-2", null)).ShouldBeTrue();
        (await fran.EnqueueMessage(TopicId, "third", "c-3", null)).ShouldBeTrue();
        var second = (await fran.GetQueue(TopicId))[0];

        if (takingBack)
        {
            await fran.TakeBackQueued(TopicId, second.Id);
        }
        else
        {
            await fran.RemoveQueued(TopicId, second.Id);
        }

        Latest(told).Items.Select(item => item.Content).ShouldBe(["third"]);
        await FinishTurnAsync("answer one");
        await AgentWasSentAsync("third");
        await FinishTurnAsync("answer three");
        await wire.Ended;
    }

    // Withdrawn too late, the message is running: the person is told so, and its files stay with it.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AWithdrawalAfterTheTurnStarted_IsAlreadySent_AndKeepsTheFiles(bool takingBack)
    {
        var fran = HubFor(new SignedInCaller());
        var wire = Read(fran.SendMessage(TopicId, "first", "c-1", null, null, CancellationToken.None));
        await AgentWasSentAsync("first");
        var photo = await UploadAsync();
        (await fran.EnqueueMessage(TopicId, "look", "c-2", null, [photo])).ShouldBeTrue();
        var queued = (await fran.GetQueue(TopicId)).ShouldHaveSingleItem();
        await FinishTurnAsync("answer one");
        await AgentWasSentAsync("look");

        var refused = await Should.ThrowAsync<HubException>(() => takingBack
            ? fran.TakeBackQueued(TopicId, queued.Id)
            : fran.RemoveQueued(TopicId, queued.Id));

        refused.Message.ShouldBe(TopicRefusals.AlreadySent);
        _attachmentStore.Find(photo.Id).ShouldNotBeNull();
        await FinishTurnAsync("answer two");
        await wire.Ended;
    }

    // The message has left the queue once it is taken: a push about it that fails must not lose the
    // words on their way back to the composer, nor fail a removal that happened.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AWithdrawalThatWon_IsAnswered_EvenWhenTellingTheTopicFails(bool takingBack)
    {
        GivenTheTopic(owner: "fran", shared: true);
        var fran = HubFor(new SignedInCaller());
        var wire = Read(fran.SendMessage(TopicId, "first", "c-1", null, null, CancellationToken.None));
        await AgentWasSentAsync("first");
        (await fran.EnqueueMessage(TopicId, "second", "c-2", null)).ShouldBeTrue();
        var second = (await fran.GetQueue(TopicId)).ShouldHaveSingleItem();
        _sender.Setup(s => s.SendToGroupAsync(
                It.IsAny<string>(), "OnQueueChanged", It.IsAny<object>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException("the backplane is down"));

        if (takingBack)
        {
            (await fran.TakeBackQueued(TopicId, second.Id)).Content.ShouldBe("second");
        }
        else
        {
            await fran.RemoveQueued(TopicId, second.Id);
        }

        (await fran.GetQueue(TopicId)).ShouldBeEmpty();
        await FinishTurnAsync("answer one");
        await wire.Ended;
    }

    // Whether the conversation still names the file cannot be told just now: the removal stands, and
    // the file is left for the sweep rather than deleted from under a message that may hold it.
    [Fact]
    public async Task ARemovalWhoseConversationCannotBeRead_StillRemoves_AndDeletesNothing()
    {
        var fran = HubFor(new SignedInCaller());
        var wire = Read(fran.SendMessage(TopicId, "first", "c-1", null, null, CancellationToken.None));
        await AgentWasSentAsync("first");
        var photo = await UploadAsync();
        (await fran.EnqueueMessage(TopicId, "look", "c-2", null, [photo])).ShouldBeTrue();
        _store.Setup(s => s.GetConversationAsync(It.IsAny<string>())).ThrowsAsync(new IOException("redis is away"));

        await fran.RemoveQueued(TopicId, (await fran.GetQueue(TopicId)).ShouldHaveSingleItem().Id);

        (await fran.GetQueue(TopicId)).ShouldBeEmpty();
        _attachmentStore.Find(photo.Id).ShouldNotBeNull();
        await FinishTurnAsync("answer one");
        await wire.Ended;
        Sent().ShouldBe(["first"]);
    }

    private Task<AttachmentReference> UploadAsync() =>
        _attachmentStore.SaveAsync(
            new AgentKey(ConversationId, "jack"), "default", "photo.png", "image/png", new MemoryStream([1, 2, 3]),
            CancellationToken.None);

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

    // The next push of the queue waits for the test to answer it; the ones after it go through.
    private HeldAnswer HoldTheNextQueuePush()
    {
        var held = new HeldAnswer();
        var first = 0;
        _sender.Setup(s => s.SendToGroupAsync(
                It.IsAny<string>(), "OnQueueChanged", It.IsAny<object>(), It.IsAny<CancellationToken>()))
            .Returns(() => Interlocked.Exchange(ref first, 1) == 0 ? held.WaitAsync() : Task.CompletedTask);
        return held;
    }

    private List<(string Group, QueueReturnedNotification Notification)> TellsOfReturns()
    {
        var told = new List<(string, QueueReturnedNotification)>();
        _sender.Setup(s => s.SendToGroupAsync(
                It.IsAny<string>(), "OnQueueReturned", It.IsAny<object>(), It.IsAny<CancellationToken>()))
            .Callback((string group, string _, object notification, CancellationToken _) =>
            {
                lock (told)
                {
                    told.Add((group, (QueueReturnedNotification)notification));
                }
            })
            .Returns(Task.CompletedTask);
        return told;
    }

    private static List<T> Snapshot<T>(List<T> told)
    {
        lock (told)
        {
            return [.. told];
        }
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

    // Everything the agent was sent until now, the inbox drained first.
    private async Task<IReadOnlyList<string>> SentSoFarAsync()
    {
        var items = await _inbox.ReceiveAsync(Subscriber, TimeSpan.Zero, CancellationToken.None);
        lock (_receivedLock)
        {
            _received.AddRange(items.Select(i => i.Message).OfType<ChannelMessageNotification>());
        }

        return Sent();
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

    // As the agent ends a turn it refused or that failed: the error, then the stream's end, one turn.
    private async Task RefuseTurnAsync(string error)
    {
        await _streamService.WriteReplyAsync(new SendReplyParams
        {
            ConversationId = ConversationId, Content = error, ContentType = ReplyContentType.Error, IsComplete = false
        });
        await _streamService.WriteReplyAsync(new SendReplyParams
        {
            ConversationId = ConversationId, Content = "", ContentType = ReplyContentType.StreamComplete, IsComplete = true
        });
    }

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

    // The inbox's clock, read as an item is put in the agent's inbox: a test acts at the moment something
    // reaches the agent, on the thread putting it there, before the caller goes on.
    private sealed class InboxClock : TimeProvider
    {
        private readonly FakeTimeProvider _time = new();
        private readonly SemaphoreSlim _reads = new(0);
        private Action? _next;

        public void WhenNextPutInTheInbox(Action act) => Volatile.Write(ref _next, act);

        // Bounded, since the put it waits for is one the code must not make.
        public void WaitForAnotherPut(TimeSpan atMost)
        {
            while (_reads.CurrentCount > 0)
            {
                _reads.Wait(0);
            }

            _reads.Wait(atMost);
        }

        public override DateTimeOffset GetUtcNow()
        {
            _reads.Release();
            Interlocked.Exchange(ref _next, null)?.Invoke();
            return _time.GetUtcNow();
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