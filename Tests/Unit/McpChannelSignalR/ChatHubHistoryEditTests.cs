using Domain.Agents;
using Domain.Channels;
using Domain.Contracts;
using Domain.Conversations;
using Domain.DTOs;
using Domain.DTOs.Channel;
using Domain.DTOs.WebChat;
using Domain.Extensions;
using Mcp.Hosting;
using McpChannelSignalR.Attachments;
using McpChannelSignalR.Hubs;
using McpChannelSignalR.Services;
using McpChannelSignalR.Settings;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Shouldly;
using Tests.Unit.McpChannelSignalR.Fixtures;

namespace Tests.Unit.McpChannelSignalR;

// A person rewriting a conversation from a page: who may, on which topics, and what everyone else on
// it is told.
public sealed class ChatHubHistoryEditTests : IDisposable
{
    private const string TopicId = "topic-edit";

    private readonly SessionService _sessionService = new();
    private readonly StreamService _streamService;
    private readonly Mock<IThreadStateStore> _store = new();
    private readonly Mock<IHubNotificationSender> _hubSender = new();
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"attachments-{Guid.NewGuid():N}");
    private readonly AttachmentStore _attachments;
    private readonly ChatHub _hub;
    private readonly ConversationIdentity _identity = ConversationIdGenerator.CreateFor(TopicId);

    public ChatHubHistoryEditTests()
    {
        var attachmentSettings = new AttachmentSettings { StoragePath = _root };
        var time = new FakeTimeProvider();
        _attachments = new AttachmentStore(
            attachmentSettings, new RetentionSettings(), new FakeConversationLiveness(), time, NullLogger<AttachmentStore>.Instance);
        var attachments = new AttachmentService(
            attachmentSettings, new AttachmentTickets(attachmentSettings, time), _attachments, NullLogger<AttachmentService>.Instance);
        _streamService = new StreamService(
            _sessionService, new Mock<IThreadStateStore>().Object, new Mock<IPushNotificationService>().Object,
            NullLogger<StreamService>.Instance);
        var approvals = new ApprovalService(
            _streamService, _sessionService, new Mock<IThreadStateStore>().Object, new Mock<IHubNotificationSender>().Object,
            new Mock<IPushNotificationService>().Object, new AccountConnections(), NullLogger<ApprovalService>.Instance);

        _hub = new ChatHub(
            _sessionService, _streamService, approvals,
            new ChannelNotificationEmitter(new ChannelInbox(new FakeTimeProvider()), DeliveryPolicy.Broadcast),
            new Mock<IAgentCatalog>().Object, _store.Object, new Mock<IPushSubscriptionStore>().Object, attachments,
            _hubSender.Object, new RetentionSettings(), new DictationSettings(), new VouchingAccounts(),
            new HouseholdSpacesAccounts(), NullLogger<ChatHub>.Instance)
        {
            Context = new SignedInCaller(),
            Groups = new Mock<IGroupManager>().Object
        };

        _store.Setup(s => s.GetMessagesAsync(Key)).ReturnsAsync([new ChatMessage(ChatRole.User, "hola")]);
    }

    private string Key => new AgentKey(_identity.ConversationId, "jonas").ToString();

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public async Task DeleteFrom_CutsTheLine_AndTellsTheTopicsAudience()
    {
        Holds(Topic());
        Answers(new Rewritten(Empty, Empty));

        await _hub.DeleteFrom("jonas", TopicId, _identity.ChatId, "e2", "e4");

        _store.Verify(s => s.RewriteAsync(Key, new CutFrom("e2", "e4")));
        _hubSender.Verify(s => s.SendToGroupAsync(
            It.IsAny<string>(), "OnHistoryChanged", It.Is<HistoryChangedNotification>(n => n.TopicId == TopicId)));
    }

    // Anyone who may continue a shared topic may rewrite it, another person's message included.
    [Fact]
    public async Task DeleteFrom_ASharedTopicOfAnotherPerson_IsRewritten()
    {
        Holds(Topic() with { OwnerId = "maria", Shared = true });
        Answers(new Rewritten(Empty, Empty));

        await _hub.DeleteFrom("jonas", TopicId, _identity.ChatId, "e2", "e4");

        _store.Verify(s => s.RewriteAsync(Key, It.IsAny<HistoryRewrite>()));
    }

    [Fact]
    public async Task DeleteFrom_AnotherPersonsPrivateTopic_IsNoSuchConversation()
    {
        Holds(Topic() with { OwnerId = "maria" });

        var refused = await Should.ThrowAsync<HubException>(() => _hub.DeleteFrom("jonas", TopicId, _identity.ChatId, "e2", "e4"));

        refused.Message.ShouldBe(TopicRefusals.NoSuchConversation);
        _store.Verify(s => s.RewriteAsync(It.IsAny<string>(), It.IsAny<HistoryRewrite>()), Times.Never);
    }

    [Fact]
    public async Task DeleteFrom_AMirroredTopic_IsRefused()
    {
        Holds(Topic() with { Mirrored = true });

        var refused = await Should.ThrowAsync<HubException>(() => _hub.DeleteFrom("jonas", TopicId, _identity.ChatId, "e2", "e4"));

        refused.Message.ShouldBe(TopicRefusals.Mirrored);
    }

    [Fact]
    public async Task DeleteFrom_WhileTheTopicStreams_IsRefused()
    {
        Holds(Topic());
        _streamService.GetOrCreateStream(TopicId, "hola", "fran", CancellationToken.None);

        var refused = await Should.ThrowAsync<HubException>(() => _hub.DeleteFrom("jonas", TopicId, _identity.ChatId, "e2", "e4"));

        refused.Message.ShouldBe(TopicRefusals.ReplyRunning);
    }

    [Theory]
    [InlineData(RewriteRefusal.Changed, TopicRefusals.Changed)]
    [InlineData(RewriteRefusal.NotFound, TopicRefusals.Changed)]
    [InlineData(RewriteRefusal.TurnInFlight, TopicRefusals.ReplyRunning)]
    public async Task DeleteFrom_TheStoresRefusal_IsSaidInItsWords(RewriteRefusal reason, string said)
    {
        Holds(Topic());
        Answers(new RewriteRefused(reason));

        var refused = await Should.ThrowAsync<HubException>(() => _hub.DeleteFrom("jonas", TopicId, _identity.ChatId, "e2", "e4"));

        refused.Message.ShouldBe(said);
        _hubSender.Verify(s => s.SendToGroupAsync(It.IsAny<string>(), "OnHistoryChanged", It.IsAny<object>()), Times.Never);
    }

    [Fact]
    public async Task SwitchVersion_ShowsTheChosenVersion_AndTellsTheTopicsAudience()
    {
        Holds(Topic());
        Answers(new Rewritten(Empty, Empty));

        await _hub.SwitchVersion("jonas", TopicId, _identity.ChatId, "u1", 0, "a2");

        _store.Verify(s => s.RewriteAsync(Key, new SwitchVersion("u1", 0, "a2")));
        _hubSender.Verify(s => s.SendToGroupAsync(It.IsAny<string>(), "OnHistoryChanged", It.IsAny<HistoryChangedNotification>()));
    }

    // Switching is rewriting what everyone on the topic reads, so it is held to the same rule.
    [Fact]
    public async Task SwitchVersion_AMirroredTopic_IsRefused()
    {
        Holds(Topic() with { Mirrored = true });

        var refused = await Should.ThrowAsync<HubException>(
            () => _hub.SwitchVersion("jonas", TopicId, _identity.ChatId, "u1", 0, "a2"));

        refused.Message.ShouldBe(TopicRefusals.Mirrored);
    }

    // An edit's re-run: the message and what followed it are put aside before the page sends the
    // edited words, and every page on the topic is told before that turn is.
    [Fact]
    public async Task PutAsideFrom_KeepsTheLineAsAVersion_AndTellsTheTopicsAudience()
    {
        Holds(Topic());
        Answers(new Rewritten(Empty, Empty));

        await _hub.PutAsideFrom("jonas", TopicId, _identity.ChatId, "u2", "a2");

        _store.Verify(s => s.RewriteAsync(Key, new PutAsideFrom("u2", "a2")));
        _hubSender.Verify(s => s.SendToGroupAsync(It.IsAny<string>(), "OnHistoryChanged", It.IsAny<HistoryChangedNotification>()));
    }

    [Fact]
    public async Task PutAsideFrom_SomethingThatIsNotAPersonsMessage_IsRefusedInItsWords()
    {
        Holds(Topic());
        Answers(new RewriteRefused(RewriteRefusal.NotRerunnable));

        var refused = await Should.ThrowAsync<HubException>(() => _hub.PutAsideFrom("jonas", TopicId, _identity.ChatId, "a2", "a2"));

        refused.Message.ShouldBe(TopicRefusals.NotRerunnable);
    }

    // A file only the deleted messages held goes with them.
    [Fact]
    public async Task DeleteFrom_DeletesTheFilesNothingHoldsAnyMore()
    {
        Holds(Topic());
        var saved = await _attachments.SaveAsync(
            new AgentKey(_identity.ConversationId, "jonas"), "default", "photo.png", "image/png", new MemoryStream([1, 2, 3]),
            CancellationToken.None);
        var withPhoto = new ChatMessage(ChatRole.User, "look");
        withPhoto.SetAttachments([saved]);
        Answers(new Rewritten(new ConversationHistory([withPhoto], new Dictionary<string, SlotVersions>()), Empty));

        await _hub.DeleteFrom("jonas", TopicId, _identity.ChatId, "e2", "e4");

        _attachments.Find(saved.Id).ShouldBeNull();
    }

    private static ConversationHistory Empty => new([], new Dictionary<string, SlotVersions>());

    private TopicMetadata Topic() =>
        new(TopicId, _identity.ChatId, _identity.ThreadId, "jonas", "Chat", DateTimeOffset.UtcNow, null,
            OwnerId: "fran", TenantId: "home");

    private void Holds(TopicMetadata topic) =>
        _store.Setup(s => s.GetTopicAsync("home", "jonas", _identity.ChatId, TopicId)).ReturnsAsync(topic);

    private void Answers(RewriteOutcome outcome) =>
        _store.Setup(s => s.RewriteAsync(Key, It.IsAny<HistoryRewrite>())).ReturnsAsync(outcome);
}