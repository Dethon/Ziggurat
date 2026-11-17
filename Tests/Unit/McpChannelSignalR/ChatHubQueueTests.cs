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
using McpChannelSignalR.McpTools;
using McpChannelSignalR.Services;
using McpChannelSignalR.Settings;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
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
    private readonly PersonTurns _turns;
    private readonly QueueNews _news;
    private readonly QueueDispatcher _dispatcher;
    private readonly ApprovalService _approvals;
    private readonly MutableAgentCatalog _catalog = new();
    private readonly HouseholdSpacesAccounts _households = new();
    private readonly Lock _receivedLock = new();
    private readonly List<ChannelMessageNotification> _received = [];
    private readonly List<ChannelCancelNotification> _cancels = [];
    private readonly List<string> _arrived = [];
    private readonly string _uploads = Path.Combine(Path.GetTempPath(), $"queue-uploads-{Guid.NewGuid():N}");
    private readonly AttachmentStore _attachmentStore;
    private readonly AttachmentService _attachments;

    public ChatHubQueueTests()
    {
        _streamService = new StreamService(
            _sessionService, _store.Object, new Mock<IPushNotificationService>().Object,
            TimeProvider.System,
            NullLogger<StreamService>.Instance);
        _inbox = new ChannelInbox(_inboxClock);
        _emitter = new ChannelNotificationEmitter(_inbox, DeliveryPolicy.Broadcast);
        _turns = HubParts.Turns(_streamService, _emitter, _accounts);
        _news = HubParts.News(_streamService, _store.Object, _sender.Object);
        _dispatcher = new QueueDispatcher(_streamService, _turns, _news, NullLogger<QueueDispatcher>.Instance);
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
            new RetentionSettings(), new DictationSettings(),
            _turns, _news, _households,
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

    // A page catching up asks for the queue while pushes may still be on their way, so the answer is
    // numbered as they are and the page keeps whichever was read later.
    [Fact]
    public async Task TheQueueAskedFor_IsAnsweredAsItIsTold_WithTheRevisionItWasReadAt()
    {
        GivenTheTopic(owner: "fran", shared: true);
        var told = TellsOfTheQueue();
        var fran = HubFor(new SignedInCaller());
        var wire = Read(fran.SendMessage(TopicId, "first", "c-1", null, null, CancellationToken.None));
        await AgentWasSentAsync("first");
        (await fran.EnqueueMessage(TopicId, "second", "c-2", null, [Photo])).ShouldBeTrue();

        var asked = await fran.GetQueue(TopicId);

        var latest = Latest(told);
        (asked.TopicId, asked.Revision, asked.SpaceSlug).ShouldBe((TopicId, latest.Revision, "default"));
        asked.Items.ShouldBe(latest.Items);
        await FinishTurnAsync("answer one");
        await AgentWasSentAsync("second");
        await FinishTurnAsync("answer two");
        await wire.Ended;
    }

    // Who hears of the queue is asked as each change is told, so sharing or unsharing the topic while
    // its reply runs moves the queue's news as it moves the stream's.
    [Fact]
    public async Task TheQueue_IsToldToTheAudienceTheTopicHasAsItChanges_ShareAndUnshareIncluded()
    {
        GivenTheTopic(owner: "fran", shared: false);
        _store.Setup(s => s.SaveTopicAsync(It.IsAny<TopicMetadata>(), It.IsAny<bool>()))
            .Callback((TopicMetadata saved, bool _) => GivenTheTopic(owner: "fran", shared: saved.Shared))
            .Returns(Task.CompletedTask);
        var told = TellsOfTheQueue();
        var fran = HubFor(new SignedInCaller());
        var wire = Read(fran.SendMessage(TopicId, "first", "c-1", null, null, CancellationToken.None));
        await AgentWasSentAsync("first");
        string[] privately = ["space:home:default:sees-all", "space:home:default:user:fran"];

        (await fran.EnqueueMessage(TopicId, "second", "c-2", null)).ShouldBeTrue();
        GroupsToldLast(told).ShouldBe(privately);

        await ShareAsync(fran, shared: true);
        (await fran.EnqueueMessage(TopicId, "third", "c-3", null)).ShouldBeTrue();
        GroupsToldLast(told).ShouldBe(["space:home:default"]);

        await ShareAsync(fran, shared: false);
        (await fran.EnqueueMessage(TopicId, "fourth", "c-4", null)).ShouldBeTrue();
        GroupsToldLast(told).ShouldBe(privately);

        foreach (var (answer, next) in new[] { ("one", "second"), ("two", "third"), ("three", "fourth") })
        {
            await FinishTurnAsync($"answer {answer}");
            await AgentWasSentAsync(next);
        }

        await FinishTurnAsync("answer four");
        await wire.Ended;
    }

    // Unsharing leaves its owner alone able to write in the topic: what other members left waiting in its
    // queue is pulled out, never run, and handed to each of them wherever they are, as a topic they no
    // longer write in cannot be where they take it from. The owner's own messages keep their place.
    [Fact]
    public async Task UnsharingATopic_PullsTheMembersQueuedMessages_AndHandsThemToThemAlone()
    {
        GivenTheTopic(owner: "fran", shared: true);
        var told = TellsOfReturns();
        var fran = HubFor(new SignedInCaller());
        var marta = HubFor(CallerAs("marta", "session-2"), "Marta");
        var wire = Read(fran.SendMessage(TopicId, "first", "c-1", null, null, CancellationToken.None));
        await AgentWasSentAsync("first");
        (await marta.EnqueueMessage(TopicId, "marta one", "c-2", null)).ShouldBeTrue();
        (await fran.EnqueueMessage(TopicId, "fran two", "c-3", null)).ShouldBeTrue();

        await ShareAsync(fran, shared: false);

        (await fran.GetQueue(TopicId)).Items.Select(i => i.Content).ShouldBe(["fran two"]);
        Snapshot(told).Single(t => t.Group == "account:marta").Notification.Unshared.ShouldBeTrue();
        marta.TakeUnshared().Select(m => m.Content).ShouldBe(["marta one"]);
        marta.TakeUnshared().ShouldBeEmpty();
        fran.TakeUnshared().ShouldBeEmpty();
        await FinishTurnAsync("answer one");
        await AgentWasSentAsync("fran two");
        await FinishTurnAsync("answer two");
        await wire.Ended;
        (await SentSoFarAsync()).ShouldBe(["first", "fran two"]);
    }

    // Words a stop handed back to a member that they had not taken yet are theirs too, and the topic they
    // were handed back in is no longer theirs to take them from.
    [Fact]
    public async Task UnsharingATopic_HandsAMemberTheWordsAStopReturnedThatTheyHadNotTaken()
    {
        GivenTheTopic(owner: "fran", shared: true);
        var fran = HubFor(new SignedInCaller());
        var marta = HubFor(CallerAs("marta", "session-2"), "Marta");
        var wire = Read(fran.SendMessage(TopicId, "first", "c-1", null, null, CancellationToken.None));
        await AgentWasSentAsync("first");
        (await marta.EnqueueMessage(TopicId, "marta one", "c-2", null)).ShouldBeTrue();
        await fran.CancelTopic(TopicId);
        await wire.Ended;

        await ShareAsync(fran, shared: false);

        marta.TakeUnshared().Select(m => m.Content).ShouldBe(["marta one"]);
    }

    // A member's message still being admitted as the owner unshares, or still being vouched for as the
    // next to run, goes the same way: handed to them, never run, and the queue goes on with the owner's.
    [Theory]
    [InlineData("admitted")]
    [InlineData("dispatched")]
    public async Task AMembersMessageInFlightAsTheTopicIsUnshared_IsHandedToThem_AndNeverRun(string stage)
    {
        GivenTheTopic(owner: "fran", shared: true);
        var fran = HubFor(new SignedInCaller());
        var marta = HubFor(CallerAs("marta", "session-2"), "Marta");
        var wire = Read(fran.SendMessage(TopicId, "first", "c-1", null, null, CancellationToken.None));
        await AgentWasSentAsync("first");
        Task<bool>? admitting = null;
        HeldAnswer vouching;
        if (stage == "admitted")
        {
            vouching = _accounts.HoldNext();
            admitting = marta.EnqueueMessage(TopicId, "marta one", "c-2", null);
            await vouching.Asked;
            (await fran.EnqueueMessage(TopicId, "fran two", "c-3", null)).ShouldBeTrue();
        }
        else
        {
            (await marta.EnqueueMessage(TopicId, "marta one", "c-2", null)).ShouldBeTrue();
            (await fran.EnqueueMessage(TopicId, "fran two", "c-3", null)).ShouldBeTrue();
            vouching = _accounts.HoldNext();
            await FinishTurnAsync("answer one");
            await vouching.Asked;
        }

        await ShareAsync(fran, shared: false);
        vouching.Answer();
        if (admitting is not null)
        {
            (await admitting).ShouldBeTrue();
            await FinishTurnAsync("answer one");
        }

        await AgentWasSentAsync("fran two");
        await FinishTurnAsync("answer two");
        await wire.Ended;
        await Eventually.Settle();
        (await SentSoFarAsync()).ShouldBe(["first", "fran two"]);
        marta.TakeUnshared().Select(m => m.Content).ShouldBe(["marta one"]);
    }

    private async Task ShareAsync(ChatHub owner, bool shared)
    {
        _store.Setup(s => s.GetTopicAsync("home", "jack", 7, TopicId))
            .ReturnsAsync(await _store.Object.GetTopicByChatIdAndThreadIdAsync("jack", 7, 42));
        await owner.SetTopicShared("jack", 7, TopicId, shared);
    }

    // A private topic's queue is read by whoever sees the topic, and to anyone else there is no such
    // conversation: not even that something waits in it.
    [Fact]
    public async Task ThePrivateTopicsQueue_IsAnsweredToWhoeverSeesTheTopic_AndAsNoSuchConversationToAnyoneElse()
    {
        GivenTheTopic(owner: "fran", shared: false);
        var fran = HubFor(new SignedInCaller());
        var marta = HubFor(CallerAs("marta", "session-2"), "Marta");
        var ana = HubFor(Caller(new ForwardedAccount("ana", "home", ["member", ForwardedAccount.SeesAllRole], SessionId: "session-3")), "Ana");
        var wire = Read(fran.SendMessage(TopicId, "first", "c-1", null, null, CancellationToken.None));
        await AgentWasSentAsync("first");
        (await fran.EnqueueMessage(TopicId, "second", "c-2", null)).ShouldBeTrue();

        var refused = await Should.ThrowAsync<HubException>(() => marta.GetQueue(TopicId));

        refused.Message.ShouldBe(TopicRefusals.NoSuchConversation);
        (await ana.GetQueue(TopicId)).Items.Select(item => item.Content).ShouldBe(["second"]);
        await FinishTurnAsync("answer one");
        await AgentWasSentAsync("second");
        await FinishTurnAsync("answer two");
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

    // A stop while a message is being admitted takes the place it held with the stream it was held on,
    // and the message goes back to its sender with the rest of the queue: the page is told it was taken,
    // so it does not send it as a new turn behind the cancel, and a stream another send opened
    // meanwhile is no part of it.
    [Fact]
    public async Task AStopWhileAMessageIsBeingQueued_ReturnsItToItsSender_AndLeavesTheNextStreamAlone()
    {
        var told = TellsOfReturns();
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
        (await queueing).ShouldBeTrue();
        await FinishTurnAsync("answer new");
        await next.Ended;
        await Eventually.Settle();

        next.Said().ShouldBe(["user: new", "agent: answer new"]);
        (await SentSoFarAsync()).ShouldBe(["first", "new"]);
        Snapshot(told).Select(Told).ShouldContain("account:fran topic-1 default c-2");
        (await fran.TakeReturned(TopicId)).Select(m => m.Content).ShouldBe(["second"]);
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
        var shown = (await fran.GetQueue(TopicId)).Items.Select(item => item.Content).ToList();

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
        (await fran.GetQueue(TopicId)).Items.ShouldBeEmpty();
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

        Snapshot(told).Select(Told).Order().ShouldBe(["account:fran topic-1 default c-2", "account:marta topic-1 default c-3"]);
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

    // A person's own send that opened the stream, still being vouched for as they press Stop: it is not
    // sent behind the cancel, with no stream left to answer on, but goes back to its sender as a queued
    // message does. A reroll's message is on the line already, so it goes nowhere: nothing is sent.
    [Theory]
    [InlineData(false, new[] { "first" })]
    [InlineData(true, new string[0])]
    public async Task AStopWhileTheSendThatOpenedTheStreamIsBeingVouchedFor_SendsNothing_AndReturnsAPersonsMessage(
        bool reroll, string[] returned)
    {
        var told = TellsOfReturns();
        var fran = HubFor(new SignedInCaller());
        var vouching = _accounts.HoldNext();
        var wire = Read(reroll
            ? fran.SendRerun(TopicId, "first", "c-1", null, null, "entry-1", CancellationToken.None)
            : fran.SendMessage(TopicId, "first", "c-1", null, null, CancellationToken.None));
        await vouching.Asked;

        await fran.CancelTopic(TopicId);
        vouching.Answer();
        await wire.Ended;
        await Eventually.Settle();

        (await ArrivedSoFarAsync()).ShouldBe(["cancel"]);
        wire.Said().ShouldBeEmpty();
        Snapshot(told).Select(Told).ShouldBe(reroll ? [] : ["account:fran topic-1 default c-1"]);
        var taken = await fran.TakeReturned(TopicId);
        taken.Select(m => m.Content).ShouldBe(returned);
        taken.Select(m => m.CorrelationId).ShouldBe(reroll ? [] : ["c-1"]);
    }

    // A send from a page that had not heard the reply start is queued behind it, and a stop landing as it
    // is admitted hands it back. That page drew it as sent, so the hub names the send in what it tells the
    // sender's pages and in what it hands back, and the page takes it off again.
    [Fact]
    public async Task ASendQueuedAsAStopLands_IsHandedBack_NamingTheSendItWas()
    {
        var told = TellsOfReturns();
        var fran = HubFor(new SignedInCaller());
        var marta = HubFor(CallerAs("marta", "session-2"), "Marta");
        var wire = Read(fran.SendMessage(TopicId, "first", "c-1", null, null, CancellationToken.None));
        await AgentWasSentAsync("first");
        var vouching = _accounts.HoldNext();
        var martas = Read(marta.SendMessage(TopicId, "second", "c-2", null, null, CancellationToken.None));
        await vouching.Asked;

        await fran.CancelTopic(TopicId);
        vouching.Answer();
        await Task.WhenAll(wire.Ended, martas.Ended);

        Snapshot(told).Select(Told).ShouldBe(["account:marta topic-1 default c-2"]);
        (await marta.TakeReturned(TopicId)).ShouldHaveSingleItem().CorrelationId.ShouldBe("c-2");
        (await SentSoFarAsync()).ShouldBe(["first"]);
    }

    // The same send refused as it is queued: its own wire says the refusal is of the send, not an answer
    // in the reply it follows, so the page that drew it takes it off and gives its words back.
    [Fact]
    public async Task ASendQueuedBehindARunningReplyAndRefused_SaysItsWireRefusesTheSend()
    {
        var fran = HubFor(new SignedInCaller());
        var marta = HubFor(CallerAs("marta", "session-2"), "Marta");
        var wire = Read(fran.SendMessage(TopicId, "first", "c-1", null, null, CancellationToken.None));
        await AgentWasSentAsync("first");
        _accounts.SignOut("session-2");

        var martas = Read(marta.SendMessage(TopicId, "second", "c-2", null, null, CancellationToken.None));
        await Eventually.Until(() => martas.Chunks().Any(c => c.Error is not null), "the send is refused");

        martas.Chunks().Single(c => c.Error is not null).RefusedSend.ShouldBeTrue();
        await FinishTurnAsync("answer one");
        await Task.WhenAll(wire.Ended, martas.Ended);
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

    // The stop ends the stream before the agent hears the cancel, so a send in between opens a stream of
    // its own: its message must reach the agent after the cancel, or the cancel would take its turn too.
    [Fact]
    public async Task ASendOpeningAStreamAsTheStopReachesTheAgent_ReachesItAfterTheCancel_AndIsAnswered()
    {
        var fran = HubFor(new SignedInCaller());
        var marta = HubFor(CallerAs("marta", "session-2"), "Marta");
        var stopped = Read(fran.SendMessage(TopicId, "first", "c-1", null, null, CancellationToken.None));
        await AgentWasSentAsync("first");

        Wire? next = null;
        _inboxClock.WhenNextPutInTheInbox(() =>
        {
            next = Read(marta.SendMessage(TopicId, "new", "c-2", null, null, CancellationToken.None));
            _inboxClock.WaitForAnotherPut(TimeSpan.FromSeconds(2));
        });
        await fran.CancelTopic(TopicId);
        await stopped.Ended;
        await AgentWasSentAsync("new");

        (await ArrivedSoFarAsync()).ShouldBe(["first", "cancel", "new"]);
        await FinishTurnAsync("answer new");
        await next!.Ended;
        next.Said().ShouldBe(["user: new", "agent: answer new"]);
    }

    // A stop handed the message back before its sender's remove or edit arrived: they are told it went
    // back to their message box, whether or not a page of theirs has taken it from the hub yet.
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task AWithdrawalAfterAStop_IsToldTheMessageWentBackToTheComposer(bool takingBack, bool takenAlready)
    {
        var fran = HubFor(new SignedInCaller());
        var marta = HubFor(CallerAs("marta", "session-2"), "Marta");
        var wire = Read(fran.SendMessage(TopicId, "first", "c-1", null, null, CancellationToken.None));
        await AgentWasSentAsync("first");
        (await marta.EnqueueMessage(TopicId, "from marta", "c-2", null)).ShouldBeTrue();
        var queuedId = (await marta.GetQueue(TopicId)).Items.ShouldHaveSingleItem().Id;
        await fran.CancelTopic(TopicId);
        await wire.Ended;
        if (takenAlready)
        {
            (await marta.TakeReturned(TopicId)).ShouldHaveSingleItem();
        }

        var refused = await Should.ThrowAsync<HubException>(() => takingBack
            ? marta.TakeBackQueued(TopicId, queuedId)
            : marta.RemoveQueued(TopicId, queuedId));

        refused.Message.ShouldBe(TopicRefusals.ReturnedToComposer);
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
        var second = (await fran.GetQueue(TopicId)).Items.First(item => item.Content == "second");

        await fran.RemoveQueued(TopicId, second.Id);

        (await fran.GetQueue(TopicId)).Items.Select(item => item.Content).ShouldBe(["third"]);
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
        var second = (await marta.GetQueue(TopicId)).Items.ShouldHaveSingleItem();

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
        var second = (await fran.GetQueue(TopicId)).Items.ShouldHaveSingleItem();

        var taken = await fran.TakeBackQueued(TopicId, second.Id);

        (taken.Id, taken.Content, taken.ConfigPatch).ShouldBe((second.Id, "second", patch));
        taken.Attachments.ShouldBe([Photo]);
        (await fran.GetQueue(TopicId)).Items.ShouldBeEmpty();
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
        var second = (await marta.GetQueue(TopicId)).Items.ShouldHaveSingleItem();

        var refused = await Should.ThrowAsync<HubException>(() => marta.TakeBackQueued(TopicId, second.Id));

        refused.Message.ShouldBe(TopicRefusals.NotTheSenders);
        (await fran.GetQueue(TopicId)).Items.ShouldHaveSingleItem().Id.ShouldBe(second.Id);
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
            var raced = (await fran.GetQueue(TopicId)).Items[0];

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
        var queued = (await fran.GetQueue(TopicId)).Items.ShouldHaveSingleItem();

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

    // A page names its files by id, and an id is no proof of holding the file: one the upload store keeps
    // for another conversation is somebody else's, or the sender's from a topic the agent's line here
    // must not reach. Refused as the message is queued, before it waits, and nothing of it is queued.
    [Fact]
    public async Task AQueuedMessageNamingAnotherConversationsFile_IsRefused_AndNotQueued()
    {
        var fran = HubFor(new SignedInCaller());
        var wire = Read(fran.SendMessage(TopicId, "first", "c-1", null, null, CancellationToken.None));
        await AgentWasSentAsync("first");
        var theirs = await UploadAsync(elsewhere: true);

        var refusal = await Should.ThrowAsync<HubException>(() => fran.EnqueueMessage(TopicId, "look", "c-2", null, [theirs]));

        refusal.Message.ShouldBe(TopicRefusals.NotYourFiles);
        (await fran.GetQueue(TopicId)).Items.ShouldBeEmpty();
        await FinishTurnAsync("answer one");
        await wire.Ended;
        (await SentSoFarAsync()).ShouldBe(["first"]);
    }

    // The same rule for a send that opens a turn: refused before its message is written or emitted.
    [Fact]
    public async Task ASendNamingAnotherConversationsFile_IsRefused_BeforeAnythingIsEmitted()
    {
        var fran = HubFor(new SignedInCaller());
        var theirs = await UploadAsync(elsewhere: true);

        var wire = Read(fran.SendMessage(TopicId, "look", "c-1", null, [theirs], CancellationToken.None));
        await wire.Ended;

        wire.Said().ShouldBe([$"error: {TopicRefusals.NotYourFiles}"]);
        (await SentSoFarAsync()).ShouldBeEmpty();
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

        await fran.RemoveQueued(TopicId, (await fran.GetQueue(TopicId)).Items[0].Id);

        _attachmentStore.Find(photo.Id).ShouldNotBeNull();
        await FinishTurnAsync("answer one");
        if (holder == "queue")
        {
            await AgentWasSentAsync("look again");
            await FinishTurnAsync("answer two");
        }

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
        var second = (await fran.GetQueue(TopicId)).Items.ShouldHaveSingleItem();

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
        var second = (await fran.GetQueue(TopicId)).Items[0];

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
        var queued = (await fran.GetQueue(TopicId)).Items.ShouldHaveSingleItem();
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
        var second = (await fran.GetQueue(TopicId)).Items.ShouldHaveSingleItem();
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

        (await fran.GetQueue(TopicId)).Items.ShouldBeEmpty();
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

        await fran.RemoveQueued(TopicId, (await fran.GetQueue(TopicId)).Items.ShouldHaveSingleItem().Id);

        (await fran.GetQueue(TopicId)).Items.ShouldBeEmpty();
        _attachmentStore.Find(photo.Id).ShouldNotBeNull();
        await FinishTurnAsync("answer one");
        await wire.Ended;
        Sent().ShouldBe(["first"]);
    }

    // Steer now: the agent is sent the message at once, flagged a steer, to come in at the running
    // turn's next tool boundary; it goes ahead of everything queued before it, and it is written on the
    // stream only once the running turn has ended, where the reply splits, never in the middle of it.
    [Fact]
    public async Task ASteer_IsSentAtOnceAheadOfTheQueue_AndWrittenOnTheStreamAfterTheRunningTurnEnds()
    {
        var fran = HubFor(new SignedInCaller());
        var wire = Read(fran.SendMessage(TopicId, "first", "c-1", null, null, CancellationToken.None));
        await AgentWasSentAsync("first");
        (await fran.EnqueueMessage(TopicId, "older", "c-2", null)).ShouldBeTrue();
        (await fran.EnqueueMessage(TopicId, "steer me", "c-3", null)).ShouldBeTrue();

        await fran.SteerQueued(TopicId, IdOf(await fran.GetQueue(TopicId), "steer me"));

        await AgentWasSentAsync("steer me");
        SentAsSteers().ShouldBe(["steer me"]);
        (await fran.GetQueue(TopicId)).Items.Select(i => i.Content).ShouldBe(["older"]);
        await Eventually.Settle();
        wire.Said().ShouldBe(["user: first"]);

        await FinishTurnAsync("cut short");
        await Eventually.Until(() => wire.Said().Contains("user: steer me"), "the steer is written once the turn ends");
        await Eventually.Settle();
        Sent().ShouldBe(["first", "steer me"]);
        await FinishTurnAsync("answer to the steer");
        await AgentWasSentAsync("older");
        await FinishTurnAsync("answer to older");

        await wire.Ended;
        wire.Said().ShouldBe([
            "user: first", "agent: cut short",
            "user: steer me", "agent: answer to the steer",
            "user: older", "agent: answer to older"
        ]);
    }

    // Two messages steered into one reply go in together (user's decision, 2026-11-17): the running turn's end
    // draws every steer waiting there, in the order they were steered, before anything of their answer, and
    // tells the agent so in its answer to that end. They are one turn, answered under the first one's key;
    // the agent ends the second's key after that answer, which ends nothing more on the page, and only then
    // does the queue go on.
    [Fact]
    public async Task SteersWaitingWhenTheRunningTurnEnds_AreAllDrawnThere_AndAnsweredAsOneTurn()
    {
        var fran = HubFor(new SignedInCaller());
        var wire = Read(fran.SendMessage(TopicId, "first", "c-1", null, null, CancellationToken.None));
        await AgentWasSentAsync("first");
        var first = TurnKeyOf("first");
        (await fran.EnqueueMessage(TopicId, "older", "c-2", null)).ShouldBeTrue();
        (await fran.EnqueueMessage(TopicId, "steer one", "c-3", null)).ShouldBeTrue();
        (await fran.EnqueueMessage(TopicId, "steer two", "c-4", null)).ShouldBeTrue();
        await fran.SteerQueued(TopicId, IdOf(await fran.GetQueue(TopicId), "steer one"));
        await fran.SteerQueued(TopicId, IdOf(await fran.GetQueue(TopicId), "steer two"));
        await AgentWasSentAsync("steer two");
        var (one, two) = (TurnKeyOf("steer one"), TurnKeyOf("steer two"));

        await StreamAsync("cut short", first);
        var receipt = await EndTurnThroughTheToolAsync(first);

        receipt.SteeredTogether.ShouldBe([one, two]);
        await Eventually.Until(() => wire.Said().Contains("user: steer two"), "both steers are drawn at the cut");
        wire.Said().ShouldBe(["user: first", "agent: cut short", "user: steer one", "user: steer two"]);
        var queue = await fran.GetQueue(TopicId);
        queue.Steering.ShouldBeEmpty();
        queue.Items.Select(i => i.Content).ShouldBe(["older"]);
        queue.RunningFor.ShouldBe("fran");

        await StreamAsync("both answered", one);
        await FinishTurnAsync(" at once", one);
        await Eventually.Settle();
        (await SentSoFarAsync()).ShouldBe(["first", "steer one", "steer two"]);
        _streamService.IsStreaming(TopicId).ShouldBeTrue();

        await EndRefusedTurnAsync(two);
        await AgentWasSentAsync("older");
        await FinishTurnAsync("answer to older");
        await wire.Ended;
        var chunks = wire.Chunks();
        var answered = chunks.ToList().FindIndex(c => c.Content == " at once");
        var older = chunks.ToList().FindIndex(c => c.UserMessage is not null && c.Content == "older");
        chunks.Skip(answered).Take(older - answered).Count(c => c.IsComplete).ShouldBe(1);
        wire.Said().ShouldBe([
            "user: first", "agent: cut short",
            "user: steer one", "user: steer two", "agent: both answered", "agent:  at once",
            "user: older", "agent: answer to older"
        ]);
    }

    // Stopped before the running turn's end was heard, every waiting steer goes back to its sender, the cancel
    // names each, and the end heard after it draws nothing together; stopped after that end drew them, none
    // goes back, since they are the turn the stop cancels, which the agent keeps as stopped.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AStop_ReturnsEverySteerWaiting_OrNoneOnceTheyWentInTogether(bool afterTheyWentIn)
    {
        var fran = HubFor(new SignedInCaller());
        var wire = Read(fran.SendMessage(TopicId, "first", "c-1", null, null, CancellationToken.None));
        await AgentWasSentAsync("first");
        var first = TurnKeyOf("first");
        (await fran.EnqueueMessage(TopicId, "steer one", "c-2", null)).ShouldBeTrue();
        (await fran.EnqueueMessage(TopicId, "steer two", "c-3", null)).ShouldBeTrue();
        await fran.SteerQueued(TopicId, IdOf(await fran.GetQueue(TopicId), "steer one"));
        await fran.SteerQueued(TopicId, IdOf(await fran.GetQueue(TopicId), "steer two"));
        await AgentWasSentAsync("steer two");
        var steers = new[] { TurnKeyOf("steer one"), TurnKeyOf("steer two") };

        var atTheCut = afterTheyWentIn ? await EndTurnThroughTheToolAsync(first) : null;
        if (afterTheyWentIn)
        {
            // Read off the wire before the stop ends it, so what the page was shown is all there.
            await Eventually.Until(() => wire.Said().Contains("user: steer two"), "both steers are drawn at the cut");
        }

        await fran.CancelTopic(TopicId);
        await wire.Ended;
        var heardAfter = afterTheyWentIn ? null : await EndTurnThroughTheToolAsync(first);
        await DrainInboxAsync();

        ChannelCancelNotification cancel;
        lock (_receivedLock)
        {
            cancel = _cancels.ShouldHaveSingleItem();
        }

        var taken = await fran.TakeReturned(TopicId);
        if (afterTheyWentIn)
        {
            atTheCut!.SteeredTogether.ShouldBe(steers);
            // Named, though empty: the agent keeps a steer a stop's cancel does not name only when the cancel
            // names what it returned at all.
            cancel.ReturnedTurnKeys.ShouldNotBeNull().ShouldBeEmpty();
            taken.ShouldBeEmpty();
            wire.Said().ShouldBe(["user: first", "user: steer one", "user: steer two"]);
        }
        else
        {
            heardAfter!.SteeredTogether.ShouldBeNull();
            heardAfter.ReturnedTurnKeys.ShouldBe(steers);
            cancel.ReturnedTurnKeys.ShouldBe(steers);
            taken.Select(m => m.Content).ShouldBe(["steer one", "steer two"]);
            wire.Said().ShouldBe(["user: first"]);
        }
    }

    // A steer the agent refused as it arrived, waiting between two others, is drawn in its place with its
    // refusal, its turn over; the two others go in together.
    [Fact]
    public async Task ARefusedSteerAmongThoseWaiting_IsDrawnWithItsRefusal_AndTheOthersGoInTogether()
    {
        var fran = HubFor(new SignedInCaller());
        var wire = Read(fran.SendMessage(TopicId, "first", "c-1", null, null, CancellationToken.None));
        await AgentWasSentAsync("first");
        var first = TurnKeyOf("first");
        (await fran.EnqueueMessage(TopicId, "steer one", "c-2", null)).ShouldBeTrue();
        (await fran.EnqueueMessage(TopicId, "steer two", "c-3", null)).ShouldBeTrue();
        (await fran.EnqueueMessage(TopicId, "steer three", "c-4", null)).ShouldBeTrue();
        (await fran.EnqueueMessage(TopicId, "older", "c-5", null)).ShouldBeTrue();
        foreach (var steer in new[] { "steer one", "steer two", "steer three" })
        {
            await fran.SteerQueued(TopicId, IdOf(await fran.GetQueue(TopicId), steer));
        }

        await AgentWasSentAsync("steer three");
        var (one, two, three) = (TurnKeyOf("steer one"), TurnKeyOf("steer two"), TurnKeyOf("steer three"));
        await RefuseTurnAsync("you were disabled", two);

        (await EndTurnThroughTheToolAsync(first)).SteeredTogether.ShouldBe([one, three]);
        await FinishTurnAsync("one and three", one);
        await EndRefusedTurnAsync(three);
        await AgentWasSentAsync("older");
        await FinishTurnAsync("answer to older");
        await wire.Ended;
        wire.Said().ShouldBe([
            "user: first",
            "user: steer one", "user: steer two", "error: you were disabled", "user: steer three",
            "agent: one and three",
            "user: older", "agent: answer to older"
        ]);
    }

    // Only the running turn's own person steers it: never another member's turn, nor a turn an agent
    // started for a schedule; and only the message's sender steers with it. Refused, it keeps its place.
    [Theory]
    [InlineData("another member's turn")]
    [InlineData("a fire")]
    [InlineData("another member's message")]
    public async Task ASteer_IsRefused_ForAnotherMembersTurn_AFire_OrAnotherMembersMessage(string running)
    {
        var fran = HubFor(new SignedInCaller());
        var marta = HubFor(CallerAs("marta", "session-2"), "Marta");
        IAsyncEnumerable<ChatStreamMessage>? opened = null;
        switch (running)
        {
            case "another member's turn":
                opened = marta.SendMessage(TopicId, "first", "c-1", null, null, CancellationToken.None);
                break;
            case "a fire":
                // As create_conversation announces a schedule's turn in the topic.
                _streamService.GetOrCreateStream(TopicId, "summarize my mail", "scheduler", CancellationToken.None);
                _streamService.TryIncrementPending(TopicId);
                break;
            default:
                opened = fran.SendMessage(TopicId, "first", "c-1", null, null, CancellationToken.None);
                break;
        }

        var wire = opened is null ? null : Read(opened);
        if (wire is not null)
        {
            await AgentWasSentAsync("first");
        }

        (await fran.EnqueueMessage(TopicId, "steer me", "c-2", null)).ShouldBeTrue();
        var steerer = running == "another member's message" ? marta : fran;

        var queued = IdOf(await fran.GetQueue(TopicId), "steer me");
        var refused = await Should.ThrowAsync<HubException>(() => steerer.SteerQueued(TopicId, queued));

        refused.Message.ShouldBe(running == "another member's message" ? TopicRefusals.NotTheSenders : TopicRefusals.NotYourTurn);
        (await fran.GetQueue(TopicId)).Items.Select(i => i.Content).ShouldBe(["steer me"]);
        await Eventually.Settle();
        SentAsSteers().ShouldBeEmpty();
    }

    // A steer sent but not started when its sender's reply is stopped goes back to them with the rest,
    // ahead of what was queued: the agent drops it in the cancel's teardown, so it never runs.
    [Fact]
    public async Task AStopWithASteerPending_ReturnsTheSteerToItsSender_AheadOfTheRest()
    {
        var fran = HubFor(new SignedInCaller());
        var wire = Read(fran.SendMessage(TopicId, "first", "c-1", null, null, CancellationToken.None));
        await AgentWasSentAsync("first");
        (await fran.EnqueueMessage(TopicId, "older", "c-2", null)).ShouldBeTrue();
        (await fran.EnqueueMessage(TopicId, "steer me", "c-3", null)).ShouldBeTrue();
        await fran.SteerQueued(TopicId, IdOf(await fran.GetQueue(TopicId), "steer me"));
        await AgentWasSentAsync("steer me");

        await fran.CancelTopic(TopicId);
        await wire.Ended;
        await FinishTurnAsync("answer one, too late");
        await Eventually.Settle();

        (await ArrivedSoFarAsync()).ShouldBe(["first", "steer me", "cancel"]);
        wire.Said().ShouldBe(["user: first"]);
        (await fran.TakeReturned(TopicId)).Select(m => m.Content).ShouldBe(["steer me", "older"]);
    }

    // The agent may have started a steer's turn already (the steered turn ended, and the hub has not heard
    // it yet): the stop's cancel names every turn key it handed back, so the agent keeps no turn of one.
    [Fact]
    public async Task AStopsCancel_NamesTheTurnKeysItHandedBack_TheSteersAmongThem()
    {
        var fran = HubFor(new SignedInCaller());
        var wire = Read(fran.SendMessage(TopicId, "first", "c-1", null, null, CancellationToken.None));
        await AgentWasSentAsync("first");
        (await fran.EnqueueMessage(TopicId, "older", "c-2", null)).ShouldBeTrue();
        (await fran.EnqueueMessage(TopicId, "steer me", "c-3", null)).ShouldBeTrue();
        var older = IdOf(await fran.GetQueue(TopicId), "older");
        await fran.SteerQueued(TopicId, IdOf(await fran.GetQueue(TopicId), "steer me"));
        await AgentWasSentAsync("steer me");

        await fran.CancelTopic(TopicId);
        await wire.Ended;
        await DrainInboxAsync();

        ChannelCancelNotification cancel;
        lock (_receivedLock)
        {
            cancel = _cancels.ShouldHaveSingleItem();
        }

        cancel.ReturnedTurnKeys.ShouldBe([TurnKeyOf("steer me"), older]);
    }

    // A stop may win the hub's race against the steered turn's end: the steer goes back to its sender
    // before the hub hears that end, and the agent, which waits on send_reply's answer before it starts
    // the next turn, would start the steer and keep it if it finished before the cancel reached it. So the
    // answer to that end names what the stop handed back, and the agent never runs it.
    [Fact]
    public async Task ATurnsEndHeardAfterAStop_IsAnsweredWithTheTurnKeysTheStopHandedBack()
    {
        var fran = HubFor(new SignedInCaller());
        var wire = Read(fran.SendMessage(TopicId, "first", "c-1", null, null, CancellationToken.None));
        await AgentWasSentAsync("first");
        (await fran.EnqueueMessage(TopicId, "steer me", "c-2", null)).ShouldBeTrue();
        await fran.SteerQueued(TopicId, IdOf(await fran.GetQueue(TopicId), "steer me"));
        await AgentWasSentAsync("steer me");
        var steer = TurnKeyOf("steer me");
        ReplyReceipt.Parse(await SendReplyAsync("still going", isComplete: false)).ReturnedTurnKeys.ShouldBeEmpty();

        await fran.CancelTopic(TopicId);
        await wire.Ended;

        ReplyReceipt.Parse(await SendReplyAsync("", isComplete: true)).ReturnedTurnKeys.ShouldBe([steer]);
    }

    // A turn's end as the agent's send_reply reaches the hub, under its turn's key, through the tool itself:
    // its stream's end, as the agent ends every turn, answered with what the agent reads.
    private async Task<ReplyReceipt> EndTurnThroughTheToolAsync(string turnKey) =>
        ReplyReceipt.Parse(await SendReplyTool.McpRun(
            ConversationId, "", ReplyContentType.StreamComplete, true, messageId: null,
            new ServiceCollection().AddSingleton<IStreamService>(_streamService).BuildServiceProvider(), turnKey));

    // As the agent's send_reply reaches the hub: through the tool itself, which answers what the agent reads.
    private Task<string> SendReplyAsync(string content, bool isComplete) =>
        SendReplyTool.McpRun(
            ConversationId, content, isComplete ? ReplyContentType.StreamComplete : ReplyContentType.Text, isComplete,
            messageId: null, new ServiceCollection().AddSingleton<IStreamService>(_streamService).BuildServiceProvider());

    // Vouched for afresh as it leaves the queue, as any turn is, for the session that queued it: a device
    // signed out since steers nothing, nor does its message steer from another device, which would send
    // what it left behind; the message keeps its place, to be refused again when its turn comes.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ASteerOfAMessageFromASessionSignedOutSince_IsRefused_AndTheMessageKeepsItsPlace(bool fromTheLaptop)
    {
        var fran = HubFor(new SignedInCaller());
        var franOnThePhone = HubFor(CallerAs("fran", "session-phone"));
        var wire = Read(fran.SendMessage(TopicId, "first", "c-1", null, null, CancellationToken.None));
        await AgentWasSentAsync("first");
        (await franOnThePhone.EnqueueMessage(TopicId, "steer me", "c-2", null)).ShouldBeTrue();

        var queued = IdOf(await fran.GetQueue(TopicId), "steer me");

        _accounts.SignOut("session-phone");
        var refused = await Should.ThrowAsync<HubException>(() => (fromTheLaptop ? fran : franOnThePhone).SteerQueued(TopicId, queued));

        refused.Message.ShouldContain("signed out");
        (await fran.GetQueue(TopicId)).Items.Select(i => i.Content).ShouldBe(["steer me"]);
        await Eventually.Settle();
        SentAsSteers().ShouldBeEmpty();
        wire.Said().ShouldBe(["user: first"]);
    }

    // Every page is told whose own turn the stream is running, the one person it offers Steer now to:
    // nobody's while an agent's turn runs or a queued turn is about to start, then that turn's sender.
    [Fact]
    public async Task TheQueue_SaysWhoseOwnTurnIsRunning_AsEachTurnStarts()
    {
        GivenTheTopic(owner: "fran", shared: true);
        var fran = HubFor(new SignedInCaller());
        var marta = HubFor(CallerAs("marta", "session-2"), "Marta");
        var wire = Read(fran.SendMessage(TopicId, "first", "c-1", null, null, CancellationToken.None));
        await AgentWasSentAsync("first");
        (await marta.EnqueueMessage(TopicId, "from marta", "c-2", null)).ShouldBeTrue();
        (await fran.GetQueue(TopicId)).RunningFor.ShouldBe("fran");
        var told = TellsOfTheQueue();

        await FinishTurnAsync("answer one");
        await AgentWasSentAsync("from marta");

        await Eventually.Until(() => Latest(told) is { RunningFor: "marta" }, "the pages are told marta's turn runs");
        (await fran.GetQueue(TopicId)).RunningFor.ShouldBe("marta");
        await FinishTurnAsync("answer two");
        await wire.Ended;
    }

    // A steer nobody is listening for comes back to the head of the queue, its sender told so, and the
    // running turn is left alone: ending it on the stream would end the reply the person is reading.
    [Fact]
    public async Task ASteerTheAgentDoesNotHear_GoesBackToTheHeadOfTheQueue_AndTheRunningTurnIsLeftAlone()
    {
        var fran = HubFor(new SignedInCaller());
        var wire = Read(fran.SendMessage(TopicId, "first", "c-1", null, null, CancellationToken.None));
        await AgentWasSentAsync("first");
        (await fran.EnqueueMessage(TopicId, "older", "c-2", null)).ShouldBeTrue();
        (await fran.EnqueueMessage(TopicId, "steer me", "c-3", null)).ShouldBeTrue();
        var queued = IdOf(await fran.GetQueue(TopicId), "steer me");

        _inboxClock.Advance(ChannelInbox._liveSubscriberFreshness + TimeSpan.FromSeconds(1));
        var refused = await Should.ThrowAsync<HubException>(() => fran.SteerQueued(TopicId, queued));

        refused.Message.ShouldBe(TopicRefusals.NotLive);
        (await fran.GetQueue(TopicId)).Items.Select(i => i.Content).ShouldBe(["steer me", "older"]);
        _streamService.IsStreaming(TopicId).ShouldBeTrue();
        wire.Said().ShouldBe(["user: first"]);
        SentAsSteers().ShouldBeEmpty();
    }

    // The agent refuses a steer as it arrives (its sender disabled meanwhile) while the turn it was meant
    // to cut still runs, and answers it as a refused turn ends, under the steer's own turn. That is the
    // steer's turn over, not the running one: the reply keeps streaming, and the steer is drawn where it
    // would have run, after that reply, answered with its refusal; then the queue goes on.
    [Fact]
    public async Task ASteerTheAgentRefusesAsItArrives_LeavesTheRunningReplyStreaming_AndIsAnsweredAfterIt()
    {
        var fran = HubFor(new SignedInCaller());
        var wire = Read(fran.SendMessage(TopicId, "first", "c-1", null, null, CancellationToken.None));
        await AgentWasSentAsync("first");
        (await fran.EnqueueMessage(TopicId, "older", "c-2", null)).ShouldBeTrue();
        (await fran.EnqueueMessage(TopicId, "steer me", "c-3", null)).ShouldBeTrue();
        await fran.SteerQueued(TopicId, IdOf(await fran.GetQueue(TopicId), "steer me"));
        await AgentWasSentAsync("steer me");

        await RefuseTurnAsync("you were disabled", TurnKeyOf("steer me"));
        await StreamAsync("still going");
        await Eventually.Settle();

        wire.Said().ShouldBe(["user: first", "agent: still going"]);
        _streamService.IsStreaming(TopicId).ShouldBeTrue();
        (await SentSoFarAsync()).ShouldBe(["first", "steer me"]);

        await FinishTurnAsync("done");
        await AgentWasSentAsync("older");
        await FinishTurnAsync("answer to older");
        await wire.Ended;
        wire.Said().ShouldBe([
            "user: first", "agent: still going", "agent: done",
            "user: steer me", "error: you were disabled",
            "user: older", "agent: answer to older"
        ]);
    }

    // The refusal's error and its stream's end are two replies, and the running turn can end between them:
    // the steer is drawn with its error then, and its turn ends on its own stream's end, once, so exactly
    // one queued message runs after it.
    [Fact]
    public async Task ASteersRefusalSplitByTheRunningTurnsEnd_EndsTheSteersTurnOnce_AndRunsOneQueuedMessage()
    {
        var fran = HubFor(new SignedInCaller());
        var wire = Read(fran.SendMessage(TopicId, "first", "c-1", null, null, CancellationToken.None));
        await AgentWasSentAsync("first");
        (await fran.EnqueueMessage(TopicId, "second", "c-2", null)).ShouldBeTrue();
        (await fran.EnqueueMessage(TopicId, "third", "c-3", null)).ShouldBeTrue();
        (await fran.EnqueueMessage(TopicId, "steer me", "c-4", null)).ShouldBeTrue();
        await fran.SteerQueued(TopicId, IdOf(await fran.GetQueue(TopicId), "steer me"));
        await AgentWasSentAsync("steer me");

        await RefuseWithErrorAsync("you were disabled", TurnKeyOf("steer me"));
        await FinishTurnAsync("done");
        await Eventually.Until(() => wire.Said().Contains("error: you were disabled"), "the steer is drawn with its error");
        await Eventually.Settle();
        (await SentSoFarAsync()).ShouldBe(["first", "steer me"]);

        await EndRefusedTurnAsync(TurnKeyOf("steer me"));
        await AgentWasSentAsync("second");
        await Eventually.Settle();
        (await SentSoFarAsync()).ShouldBe(["first", "steer me", "second"]);

        await FinishTurnAsync("answer two");
        await AgentWasSentAsync("third");
        await FinishTurnAsync("answer three");
        await wire.Ended;
        wire.Said().ShouldBe([
            "user: first", "agent: done",
            "user: steer me", "error: you were disabled",
            "user: second", "agent: answer two",
            "user: third", "agent: answer three"
        ]);
    }

    // The running turn itself failing, with a steer waiting, is still that turn's end: the reply splits
    // there and the steer's turn starts, answered as any other.
    [Fact]
    public async Task TheRunningTurnFailingWithASteerWaiting_SplitsThere_AndTheSteerRuns()
    {
        var fran = HubFor(new SignedInCaller());
        var wire = Read(fran.SendMessage(TopicId, "first", "c-1", null, null, CancellationToken.None));
        await AgentWasSentAsync("first");
        (await fran.EnqueueMessage(TopicId, "steer me", "c-2", null)).ShouldBeTrue();
        await fran.SteerQueued(TopicId, IdOf(await fran.GetQueue(TopicId), "steer me"));
        await AgentWasSentAsync("steer me");

        await RefuseTurnAsync("the model failed");
        await Eventually.Until(() => wire.Said().Contains("user: steer me"), "the steer is written once the turn ends");
        _streamService.IsStreaming(TopicId).ShouldBeTrue();
        await StreamAsync("answer to the steer, ", TurnKeyOf("steer me"));
        await FinishTurnAsync("done");

        await wire.Ended;
        wire.Said().ShouldBe([
            "user: first", "error: the model failed",
            "user: steer me", "agent: answer to the steer, ", "agent: done"
        ]);
    }

    // A stop before the running turn ends returns a steer the agent already refused with the rest of what
    // waited, to its sender's composer, its refusal never drawn: nothing after a stop is.
    [Fact]
    public async Task AStopWithARefusedSteerHeld_ReturnsTheSteerToItsSender_ItsRefusalNeverDrawn()
    {
        var fran = HubFor(new SignedInCaller());
        var wire = Read(fran.SendMessage(TopicId, "first", "c-1", null, null, CancellationToken.None));
        await AgentWasSentAsync("first");
        (await fran.EnqueueMessage(TopicId, "older", "c-2", null)).ShouldBeTrue();
        (await fran.EnqueueMessage(TopicId, "steer me", "c-3", null)).ShouldBeTrue();
        await fran.SteerQueued(TopicId, IdOf(await fran.GetQueue(TopicId), "steer me"));
        await AgentWasSentAsync("steer me");
        await RefuseTurnAsync("you were disabled", TurnKeyOf("steer me"));

        await fran.CancelTopic(TopicId);
        await wire.Ended;

        wire.Said().ShouldBe(["user: first"]);
        (await fran.TakeReturned(TopicId)).Select(m => m.Content).ShouldBe(["steer me", "older"]);
    }

    // A schedule's turn attached to the topic while the person's turn runs, ending first, is the fire's
    // turn over, not the person's: the steer they sent into their own turn is written once that turn ends.
    [Fact]
    public async Task AFireEndingWhileTheSteeredTurnRuns_LeavesTheSteerWaiting_UntilThePersonsTurnEnds()
    {
        var fran = HubFor(new SignedInCaller());
        var wire = Read(fran.SendMessage(TopicId, "first", "c-1", null, null, CancellationToken.None));
        await AgentWasSentAsync("first");
        (await fran.EnqueueMessage(TopicId, "steer me", "c-2", null)).ShouldBeTrue();
        await fran.SteerQueued(TopicId, IdOf(await fran.GetQueue(TopicId), "steer me"));
        await AgentWasSentAsync("steer me");

        // As create_conversation announces a schedule's turn in the topic, and send_reply ends it.
        _streamService.GetOrCreateStream(TopicId, "summarize my mail", "scheduler", CancellationToken.None);
        _streamService.TryIncrementPending(TopicId);
        await _streamService.WriteReplyAsync(new SendReplyParams
        {
            ConversationId = ConversationId, Content = "your mail", ContentType = ReplyContentType.Text,
            IsComplete = true, MessageId = "fire", TurnKey = TurnKey.Mint(), AgentInitiated = true
        });
        await Eventually.Settle();

        wire.Said().ShouldNotContain("user: steer me");
        _streamService.IsStreaming(TopicId).ShouldBeTrue();

        await FinishTurnAsync("done");
        await Eventually.Until(() => wire.Said().Contains("user: steer me"), "the steer is written once the person's turn ends");
        await FinishTurnAsync("answer to the steer");
        await wire.Ended;
        wire.Said().ShouldBe([
            "user: first", "agent: your mail", "agent: done",
            "user: steer me", "agent: answer to the steer"
        ]);
    }

    // A steered message has left the queue but not yet been drawn: until the running turn ends, every
    // page shows it apart from the queue, as steering, and is told once it is drawn.
    [Fact]
    public async Task ASteer_IsToldAsSteering_ApartFromTheQueue_UntilTheRunningTurnEndsAndDrawsIt()
    {
        GivenTheTopic(owner: "fran", shared: false);
        var fran = HubFor(new SignedInCaller());
        var wire = Read(fran.SendMessage(TopicId, "first", "c-1", null, null, CancellationToken.None));
        await AgentWasSentAsync("first");
        (await fran.EnqueueMessage(TopicId, "older", "c-2", null)).ShouldBeTrue();
        (await fran.EnqueueMessage(TopicId, "steer me", "c-3", null)).ShouldBeTrue();
        var told = TellsOfTheQueue();

        await fran.SteerQueued(TopicId, IdOf(await fran.GetQueue(TopicId), "steer me"));

        var steering = await fran.GetQueue(TopicId);
        steering.Items.Select(i => i.Content).ShouldBe(["older"]);
        (steering.Steering ?? []).Select(i => i.Content).ShouldBe(["steer me"]);
        (Latest(told).Steering ?? []).Select(i => i.Content).ShouldBe(["steer me"]);

        await FinishTurnAsync("cut short");

        await Eventually.Until(
            () => Latest(told) is { Steering: [] or null } latest && latest.Revision > steering.Revision,
            "the pages are told the steer was drawn");
        await Eventually.Until(() => wire.Said().Contains("user: steer me"), "the steer is drawn");
        (await fran.GetQueue(TopicId)).Steering.ShouldBeEmpty();
        await FinishTurnAsync("answer to the steer");
        await AgentWasSentAsync("older");
        await FinishTurnAsync("answer to older");
        await wire.Ended;
    }

    // A stop with only a steer waiting hands it back, and the pages are told it no longer steers.
    [Fact]
    public async Task AStopWithOnlyASteerWaiting_TellsThePagesItNoLongerSteers()
    {
        GivenTheTopic(owner: "fran", shared: false);
        var fran = HubFor(new SignedInCaller());
        var wire = Read(fran.SendMessage(TopicId, "first", "c-1", null, null, CancellationToken.None));
        await AgentWasSentAsync("first");
        (await fran.EnqueueMessage(TopicId, "steer me", "c-2", null)).ShouldBeTrue();
        var told = TellsOfTheQueue();
        await fran.SteerQueued(TopicId, IdOf(await fran.GetQueue(TopicId), "steer me"));
        var steering = Latest(told);

        await fran.CancelTopic(TopicId);
        await wire.Ended;

        Latest(told).Revision.ShouldBeGreaterThan(steering.Revision);
        (Latest(told).Steering ?? []).ShouldBeEmpty();
    }

    // A schedule's turn attaching to the topic while the person's own turn runs makes the running turn
    // nobody's to steer, and every page is told at once: a page still offering Steer now would only be
    // refused for it.
    [Fact]
    public async Task AFireAttachingMidTurn_TellsThePagesTheRunningTurnIsNobodysToSteer()
    {
        GivenTheTopic(owner: "fran", shared: false);
        var fran = HubFor(new SignedInCaller());
        var wire = Read(fran.SendMessage(TopicId, "first", "c-1", null, null, CancellationToken.None));
        await AgentWasSentAsync("first");
        (await fran.EnqueueMessage(TopicId, "steer me", "c-2", null)).ShouldBeTrue();
        var before = await fran.GetQueue(TopicId);
        before.RunningFor.ShouldBe("fran");
        var told = TellsOfTheQueue();

        await CreateConversationTool.McpRun(
            "jack", "", "scheduler", ToolServices(), initialPrompt: "summarize my mail",
            existingConversationId: ConversationId);

        await Eventually.Until(
            () => Snapshot(told).Any(t => t.Notification.Revision > before.Revision && t.Notification.RunningFor is null),
            "the pages are told nobody's own turn runs");
        Latest(told).Items.Select(i => i.Content).ShouldBe(["steer me"]);
        await FinishTurnAsync("done");
    }

    // What create_conversation reads off the server's services, over this hub's own.
    private ServiceProvider ToolServices() =>
        new ServiceCollection()
            .AddSingleton(_sessionService)
            .AddSingleton(_streamService)
            .AddSingleton(_store.Object)
            .AddSingleton(_sender.Object)
            .BuildServiceProvider();

    private static string IdOf(QueueChangedNotification queue, string content) =>
        queue.Items.Single(i => i.Content == content).Id;

    // The key the agent answers a message's turn under: the one the channel sent it with, else one of its own.
    private string TurnKeyOf(string content)
    {
        lock (_receivedLock)
        {
            return _received.Single(m => m.Content == content).TurnKey ?? TurnKey.Mint();
        }
    }

    private IReadOnlyList<string> SentAsSteers()
    {
        lock (_receivedLock)
        {
            return [.. _received.Where(m => m.Steer).Select(m => m.Content)];
        }
    }

    private Task<AttachmentReference> UploadAsync(bool elsewhere = false) =>
        _attachmentStore.SaveAsync(
            new AgentKey(elsewhere ? "8:99" : ConversationId, "jack"), "default", "photo.png", "image/png",
            new MemoryStream([1, 2, 3]), CancellationToken.None);

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

    // A return's telling, as one line: whose pages, which topic and space, and which sends it names.
    private static string Told((string Group, QueueReturnedNotification Notification) told) =>
        $"{told.Group} {told.Notification.TopicId} {told.Notification.SpaceSlug} {string.Join(",", told.Notification.CorrelationIds ?? [])}";

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

    private static IReadOnlyList<string> GroupsToldLast(List<(string Group, QueueChangedNotification Notification)> told)
    {
        var latest = Latest(told).Revision;
        lock (told)
        {
            return [.. told.Where(t => t.Notification.Revision == latest).Select(t => t.Group).Order()];
        }
    }

    private static SignedInCaller CallerAs(string userId, string sessionId) =>
        Caller(new ForwardedAccount(userId, "home", ["member"], SessionId: sessionId));

    private static SignedInCaller Caller(ForwardedAccount account)
    {
        var caller = new SignedInCaller();
        caller.Items[ChatHub.AccountItem] = account;
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
        await DrainInboxAsync();
        return Sent();
    }

    // What reached the agent, in the order it arrived: each message's words, and "cancel" for a stop's.
    private async Task<IReadOnlyList<string>> ArrivedSoFarAsync()
    {
        await DrainInboxAsync();
        lock (_receivedLock)
        {
            return [.. _arrived];
        }
    }

    private async Task AgentWasSentAsync(string content) =>
        await Eventually.Until(async () =>
        {
            await DrainInboxAsync();
            lock (_receivedLock)
            {
                return _received.Any(m => m.Content == content);
            }
        }, $"the agent is sent \"{content}\"");

    private async Task DrainInboxAsync()
    {
        var items = await _inbox.ReceiveAsync(Subscriber, TimeSpan.Zero, CancellationToken.None);
        lock (_receivedLock)
        {
            _received.AddRange(items.Select(i => i.Message).OfType<ChannelMessageNotification>());
            _cancels.AddRange(items.Select(i => i.Cancel).OfType<ChannelCancelNotification>());
            _arrived.AddRange(items.Select(i => i.Message?.Content ?? (i.Cancel is not null ? "cancel" : i.Kind.ToString())));
        }
    }

    // As send_reply finishes a turn: its last words, marked complete.
    private Task FinishTurnAsync(string answer, string? turnKey = null) =>
        _streamService.WriteReplyAsync(new SendReplyParams
        {
            ConversationId = ConversationId,
            Content = answer,
            ContentType = ReplyContentType.Text,
            IsComplete = true,
            MessageId = answer,
            TurnKey = turnKey
        });

    // As send_reply streams a turn's words, the turn not over yet.
    private Task StreamAsync(string words, string? turnKey = null, bool? agentInitiated = null) =>
        _streamService.WriteReplyAsync(new SendReplyParams
        {
            ConversationId = ConversationId,
            Content = words,
            ContentType = ReplyContentType.Text,
            IsComplete = false,
            MessageId = words,
            TurnKey = turnKey,
            AgentInitiated = agentInitiated
        });

    // As the agent ends a turn it refused or that failed: the error, then the stream's end, one turn.
    private async Task RefuseTurnAsync(string error, string? turnKey = null)
    {
        await RefuseWithErrorAsync(error, turnKey);
        await EndRefusedTurnAsync(turnKey);
    }

    private Task RefuseWithErrorAsync(string error, string? turnKey = null) =>
        _streamService.WriteReplyAsync(new SendReplyParams
        {
            ConversationId = ConversationId, Content = error, ContentType = ReplyContentType.Error, IsComplete = false,
            TurnKey = turnKey
        });

    private Task EndRefusedTurnAsync(string? turnKey = null) =>
        _streamService.WriteReplyAsync(new SendReplyParams
        {
            ConversationId = ConversationId, Content = "", ContentType = ReplyContentType.StreamComplete, IsComplete = true,
            TurnKey = turnKey
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

    // The inbox's clock, read as an item is put in the agent's inbox: a test acts at the moment something
    // reaches the agent, on the thread putting it there, before the caller goes on.
    private sealed class InboxClock : TimeProvider
    {
        private readonly FakeTimeProvider _time = new();
        private readonly SemaphoreSlim _reads = new(0);
        private Action? _next;

        public void WhenNextPutInTheInbox(Action act) => Volatile.Write(ref _next, act);

        public void Advance(TimeSpan by) => _time.Advance(by);

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