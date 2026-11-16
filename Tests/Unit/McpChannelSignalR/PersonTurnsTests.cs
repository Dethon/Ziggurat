using Domain.Agents;
using Domain.Channels;
using Domain.Contracts;
using Domain.Conversations;
using Domain.DTOs;
using Domain.DTOs.Channel;
using Domain.DTOs.Household;
using Domain.DTOs.WebChat;
using Domain.Security;
using Mcp.Hosting;
using McpChannelSignalR.Hubs;
using McpChannelSignalR.Internal;
using McpChannelSignalR.Services;
using McpChannelSignalR.Settings;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Shouldly;
using Tests.Unit.McpChannelSignalR.Fixtures;

namespace Tests.Unit.McpChannelSignalR;

// A person's message held for later (a queued message, dispatched when the running turn ends) is sent
// with no hub call in hand: from a snapshot of who sent it, taken as the hub accepted it. It must reach
// the agent exactly as the live send would have, and be vouched for afresh when it leaves, so a session
// signed out while it waited sends nothing.
public sealed class PersonTurnsTests : IDisposable
{
    private const string TopicId = "topic-1";
    private const string Subscriber = "channel-signalr";

    private static readonly AttachmentReference _photo = new()
    {
        Id = "7-42/abc",
        FileName = "photo.png",
        MediaType = "image/png",
        SizeBytes = 4
    };

    private static readonly AgentConfigPatch _patch = new() { Model = "sees/pictures" };

    private readonly SessionService _sessionService = new();
    private readonly StreamService _streamService;
    private readonly ChannelInbox _inbox = new(new FakeTimeProvider());
    private readonly RevocableAccounts _accounts = new();
    private readonly ChannelNotificationEmitter _emitter;
    private readonly ChatHub _hub;

    public PersonTurnsTests()
    {
        _streamService = new StreamService(
            _sessionService, new Mock<IThreadStateStore>().Object, new Mock<IPushNotificationService>().Object,
            TimeProvider.System,
            NullLogger<StreamService>.Instance);
        _emitter = new ChannelNotificationEmitter(_inbox, DeliveryPolicy.Broadcast);

        var catalog = new MutableAgentCatalog();
        catalog.Replace([
            new AgentCatalogEntry("jack", "Jack", null, DefaultModel: "text/only",
                PatchableModels: [new PatchableModel("sees/pictures", "Sees", [AttachmentKind.Image])],
                DefaultModelAttachmentKinds: [])
        ]);
        var households = new HouseholdSpacesAccounts();
        households.SavedAgents["home"] = new AgentsSection
        {
            Agents = [new HouseholdAgent { Id = "jack", Definition = new AgentDefinition
            {
                Id = "jack", Name = "Jack", Model = "text/only", McpServerEndpoints = []
            } }]
        };

        _hub = new ChatHub(
            _sessionService, _streamService, approvalService: null!, _emitter, catalog,
            new Mock<IThreadStateStore>().Object, pushSubscriptionStore: null!, attachmentService: null!,
            new Mock<IHubNotificationSender>().Object, new RetentionSettings(), new DictationSettings(),
            HubParts.Turns(_streamService, _emitter, _accounts),
            HubParts.News(_streamService, new Mock<IThreadStateStore>().Object, new Mock<IHubNotificationSender>().Object),
            households, NullLogger<ChatHub>.Instance)
        {
            Context = new SignedInCaller()
        };
        _hub.Context.Items[ChatHub.DisplayNameItem] = "Fran";

        _sessionService.StartSession(TopicId, "jack", 7, 42, "default", "Chat");
    }

    public void Dispose() => _streamService.Dispose();

    private PersonTurns Turns => HubParts.Turns(_streamService, _emitter, _accounts);

    private SenderSnapshot Snapshot()
    {
        _sessionService.TryGetSession(TopicId, out var session).ShouldBeTrue();
        return SenderSnapshot.Of(
            (ForwardedAccount)_hub.Context.Items[ChatHub.AccountItem]!, "Fran", TopicId, session!);
    }

    [Fact]
    public async Task ASnapshot_SendsTheAgentWhatTheLiveSendSent()
    {
        var live = await LiveSentAsync(ct => _hub.SendMessage(TopicId, "hola", "c-1", _patch, [_photo], ct));

        var message = new TurnMessage("hola", "c-1", _patch, [_photo]);
        var (fromSnapshot, userMessage) = await SnapshotSentAsync(message);

        foreach (var sent in new[] { live, fromSnapshot })
        {
            sent.Sender.ShouldBe("Fran");
            sent.Principal.ShouldBe(new Principal(ChannelIds.SignalR, "fran", new Account("home", "fran")));
            sent.ConversationId.ShouldBe("7:42");
            sent.AgentId.ShouldBe("jack");
            sent.Content.ShouldBe("hola");
            sent.ConfigPatch.ShouldBe(_patch);
            sent.Attachments.ShouldBe([_photo]);
            sent.RerunOf.ShouldBeNull();
            sent.Continuation.ShouldBeFalse();
            var asserted = await TestAssertions.Verifier().VerifyAsync(sent.Assertion, ChannelIds.SignalR, CancellationToken.None);
            asserted.ShouldNotBeNull().UserId.ShouldBe("fran");
            asserted.TenantId.ShouldBe("home");
            asserted.SessionId.ShouldBe("session-1");
        }

        userMessage!.Content.ShouldBe("hola");
        userMessage.UserMessage!.SenderId.ShouldBe("fran");
        userMessage.UserMessage.SenderName.ShouldBe("Fran");
        userMessage.UserMessage.CorrelationId.ShouldBe("c-1");
        userMessage.Attachments.ShouldBe([_photo]);
    }

    [Fact]
    public async Task ASnapshot_CarriesARerunAndAContinueAsTheLiveSendsDo()
    {
        var (rerun, _) = await SnapshotSentAsync(new TurnMessage("hola", null, null, null, RerunOf: "u1"));
        var (continued, written) = await SnapshotSentAsync(
            new TurnMessage(Continuation.Instruction, null, null, null, Continuation: true));

        rerun.RerunOf.ShouldBe("u1");
        continued.Continuation.ShouldBeTrue();
        continued.Content.ShouldBe(Continuation.Instruction);
        written.ShouldBeNull();
    }

    [Fact]
    public async Task ASessionSignedOutAfterTheSnapshot_IsRefusedSignedOutOnTheStream_AndSendsNothing()
    {
        var sender = Snapshot();
        _accounts.Revoked = true;

        var stream = await OpenStreamAsync();
        var vouched = await Turns.VouchAsync(sender, CancellationToken.None);

        vouched.ShouldBeNull();
        (await EndOfAsync(stream)).ShouldContain(c => c.Error != null && c.Error.Contains("signed out") && c.IsComplete);
        (await ReceivedAsync()).ShouldBeEmpty();
    }

    [Fact]
    public async Task AnAccountsServiceUnreachableWhenTheSnapshotIsSent_AnswersCouldNotBeChecked_AndSendsNothing()
    {
        var sender = Snapshot();
        _accounts.Fails = true;

        var stream = await OpenStreamAsync();
        var vouched = await Turns.VouchAsync(sender, CancellationToken.None);

        vouched.ShouldBeNull();
        (await EndOfAsync(stream))
            .ShouldContain(c => c.Error != null && c.Error.Contains("could not be checked") && c.IsComplete);
        (await ReceivedAsync()).ShouldBeEmpty();
    }

    // A queued message is sent into a stream already running, as an enqueued one is today.
    private async Task<System.Threading.Channels.ChannelReader<ChatStreamMessage>> OpenStreamAsync()
    {
        await _inbox.ReceiveAsync(Subscriber, TimeSpan.Zero, CancellationToken.None);
        var (channel, _) = _streamService.GetOrCreateStream(TopicId, "first", "fran", CancellationToken.None);
        _streamService.TryIncrementPending(TopicId).ShouldBeTrue();
        return channel.Subscribe();
    }

    private static async Task<IReadOnlyList<ChatStreamMessage>> EndOfAsync(
        System.Threading.Channels.ChannelReader<ChatStreamMessage> stream)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var chunks = new List<ChatStreamMessage>();
        await foreach (var chunk in stream.ReadAllAsync(cts.Token))
        {
            chunks.Add(chunk);
        }

        return chunks;
    }

    private async Task<IReadOnlyList<ChannelMessageNotification>> ReceivedAsync() =>
        [.. (await _inbox.ReceiveAsync(Subscriber, TimeSpan.Zero, CancellationToken.None))
            .Select(i => i.Message).OfType<ChannelMessageNotification>()];

    private async Task<(ChannelMessageNotification Sent, ChatStreamMessage? Written)> SnapshotSentAsync(TurnMessage message)
    {
        var stream = await OpenStreamAsync();
        var sender = Snapshot();

        var vouched = (await Turns.VouchAsync(sender, CancellationToken.None)).ShouldNotBeNull();
        await Turns.WriteMessageAsync(sender, message);
        (await Turns.EmitAsync(vouched, message, CancellationToken.None)).ShouldBeTrue();

        var sent = (await ReceivedAsync()).ShouldHaveSingleItem();
        stream.TryRead(out var written);
        _streamService.CancelStream(TopicId);
        return (sent, written);
    }

    // The live send waits for a reply nobody gives here, so it is read until the agent has been told, then let go.
    private async Task<ChannelMessageNotification> LiveSentAsync(
        Func<CancellationToken, IAsyncEnumerable<ChatStreamMessage>> send)
    {
        await _inbox.ReceiveAsync(Subscriber, TimeSpan.Zero, CancellationToken.None);
        using var cts = new CancellationTokenSource();
        var reading = Task.Run(async () =>
        {
            await foreach (var _ in send(cts.Token))
            {
            }
        });

        ChannelMessageNotification? emitted = null;
        await Eventually.Until(async () =>
        {
            emitted ??= (await ReceivedAsync()).FirstOrDefault();
            return emitted is not null;
        }, "the agent is told of the turn");
        await cts.CancelAsync();
        await reading.ContinueWith(_ => { });
        _streamService.CancelStream(TopicId);
        return emitted!;
    }

    // The accounts service as a queued message meets it when it leaves: live, or signed out since, or
    // not answering at all.
    private sealed class RevocableAccounts : ISessionAccounts
    {
        public bool Revoked { get; set; }
        public bool Fails { get; set; }

        public Task<ResolvedAccount?> SessionAsync(string userId, string sessionId, CancellationToken ct)
        {
            if (Fails)
            {
                throw new HttpRequestException("the accounts service is not answering");
            }

            var account = new Account("home", userId);
            return Task.FromResult<ResolvedAccount?>(Revoked
                ? null
                : new ResolvedAccount(account, userId, TestAssertions.Of(account, ChannelIds.SignalR, sessionId: sessionId)));
        }
    }
}