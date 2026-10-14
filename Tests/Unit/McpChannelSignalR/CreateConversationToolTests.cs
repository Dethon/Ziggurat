using Domain.Contracts;
using Domain.DTOs;
using Domain.DTOs.Channel;
using Domain.DTOs.WebChat;
using Infrastructure.Conversations;
using McpChannelSignalR.McpTools;
using McpChannelSignalR.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using Shouldly;

namespace Tests.Unit.McpChannelSignalR;

public class CreateConversationToolTests
{
    private readonly SessionService _sessionService = new();
    private readonly StreamService _streamService;
    private readonly Mock<IHubNotificationSender> _hubSender = new();
    private readonly TopicTable _topics = new();
    private readonly IServiceProvider _services;

    public CreateConversationToolTests()
    {
        _streamService = new StreamService(
            _sessionService,
            new Mock<IPushNotificationService>().Object,
            new Mock<ILogger<StreamService>>().Object);
        _services = new ServiceCollection()
            .AddSingleton(_sessionService)
            .AddSingleton(_streamService)
            .AddSingleton(_hubSender.Object)
            .AddSingleton<IConversationFactory>(new ConversationFactory(_topics.Store, TimeProvider.System))
            .BuildServiceProvider();
    }

    private Task<string> AttachAsync(string conversationId, string prompt = "[download-complete] film.mkv")
    {
        return CreateConversationTool.McpRun(
            "jack", string.Empty, "fran", _services,
            initialPrompt: prompt, address: null, existingConversationId: conversationId);
    }

    [Fact]
    public async Task McpRun_AttachWithSession_CreatesStreamSeededWithPromptAndSender()
    {
        _sessionService.StartSession("topic-1", "jack", 7, 42, "default", "Downloads");

        var result = await AttachAsync("7:42");

        result.ShouldBe("7:42");
        _streamService.IsStreaming("topic-1").ShouldBeTrue();
        var state = _streamService.GetStreamState("topic-1");
        state.ShouldNotBeNull();
        state.CurrentPrompt.ShouldBe("[download-complete] film.mkv");
        state.CurrentSenderId.ShouldBe("fran");
        var userBubble = state.BufferedMessages.ShouldHaveSingleItem();
        userBubble.Content.ShouldBe("[download-complete] film.mkv");
        userBubble.UserMessage.ShouldNotBeNull();
        userBubble.UserMessage.SenderId.ShouldBe("fran");
    }

    [Fact]
    public async Task McpRun_AttachWithoutSession_ReturnsIdWithoutSideEffects()
    {
        var result = await AttachAsync("9:99");

        result.ShouldBe("9:99");
        _streamService.IsStreaming("9:99").ShouldBeFalse();
        _hubSender.Verify(s => s.SendToGroupAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<object>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task McpRun_AttachDuringActiveUserTurn_JoinsStreamWithoutStealingTeardown()
    {
        // A download alert can land while the user's own turn is streaming. The attach
        // joins the existing stream (one more pending turn); the stream survives the
        // first StreamComplete and tears down after the second.
        _sessionService.StartSession("topic-1", "jack", 7, 42, "default", "Downloads");
        _streamService.GetOrCreateStream("topic-1", "user prompt", "fran", CancellationToken.None);
        _streamService.TryIncrementPending("topic-1");

        await AttachAsync("7:42");
        await _streamService.WriteReplyAsync(new SendReplyParams
        {
            ConversationId = "7:42", Content = string.Empty,
            ContentType = ReplyContentType.StreamComplete, IsComplete = true
        });
        _streamService.IsStreaming("topic-1").ShouldBeTrue();

        await _streamService.WriteReplyAsync(new SendReplyParams
        {
            ConversationId = "7:42", Content = string.Empty,
            ContentType = ReplyContentType.StreamComplete, IsComplete = true
        });
        _streamService.IsStreaming("topic-1").ShouldBeFalse();
    }
    // A later target of a fan-out names the conversation the first target anchored, and a topic name:
    // the conversation is shown here, so a topic is minted over that very conversation — its history
    // key is the one the agent writes the turn under — the first time nothing holds it.
    [Fact]
    public async Task McpRun_AFanOutTargetOnAConversationNoTopicHolds_MintsATopicOverThatConversation()
    {
        var result = await ShowAsync("11:22", "a plate of paella");

        result.ShouldBe("11:22");
        var topic = _topics.Saved.ShouldHaveSingleItem();
        (topic.ChatId, topic.ThreadId, topic.AgentId, topic.Name).ShouldBe((11L, 22L, "sugary", "Meal estimate"));
        _sessionService.GetTopicIdByConversationId("11:22").ShouldBe(topic.TopicId);
        _streamService.GetStreamState(topic.TopicId).ShouldNotBeNull().CurrentPrompt.ShouldBe("a plate of paella");
        _hubSender.Verify(s => s.SendToGroupAsync(
            "space:default", "OnTopicChanged",
            It.Is<TopicChangedNotification>(n => n.TopicId == topic.TopicId && n.ChangeType == TopicChangeType.Created),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task McpRun_ALaterTurnOnTheSameConversation_AttachesToTheTopicItMinted()
    {
        await ShowAsync("11:22", "a plate of paella");
        var topicId = _topics.Saved.ShouldHaveSingleItem().TopicId;
        await _streamService.WriteReplyAsync(new SendReplyParams
        {
            ConversationId = "11:22", Content = string.Empty,
            ContentType = ReplyContentType.StreamComplete, IsComplete = true
        });

        var result = await ShowAsync("11:22", "about 300 g");

        result.ShouldBe("11:22");
        _topics.Saved.ShouldHaveSingleItem();
        _streamService.GetStreamState(topicId).ShouldNotBeNull().BufferedMessages
            .ShouldContain(m => m.Content == "about 300 g" && m.UserMessage != null);
        _hubSender.Verify(s => s.SendToGroupAsync(
            "space:default", "OnStreamStarted",
            It.Is<StreamStartedNotification>(n => n.TopicId == topicId),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    // A restart forgets the sessions and keeps the topics. The topic already holds the conversation,
    // so nothing is minted twice: the turn is delivered by persistence, as any turn with no live
    // session is (ADR 0035).
    [Fact]
    public async Task McpRun_AFanOutTargetOnAConversationATopicAlreadyHolds_MintsNoSecondTopic()
    {
        await ShowAsync("11:22", "a plate of paella");
        _sessionService.EndSession(_topics.Saved.Single().TopicId);

        var result = await ShowAsync("11:22", "about 300 g");

        result.ShouldBe("11:22");
        _topics.Saved.ShouldHaveSingleItem();
    }

    // Only a conversation spelled as WebChat spells one has a history a topic can read, so any other
    // id is attached to exactly as before and shown nowhere.
    [Fact]
    public async Task McpRun_AFanOutTargetOnAConversationNoTopicCanHold_MintsNothing()
    {
        var result = await ShowAsync("sched-morning-news-1", "a plate of paella");

        result.ShouldBe("sched-morning-news-1");
        _topics.Saved.ShouldBeEmpty();
    }

    private Task<string> ShowAsync(string conversationId, string prompt) =>
        CreateConversationTool.McpRun(
            "sugary", "Meal estimate", "sugary-app", _services,
            initialPrompt: prompt, address: null, existingConversationId: conversationId);

    // The two calls the factory makes of the thread store, kept in memory.
    private sealed class TopicTable
    {
        private readonly List<TopicMetadata> _saved = [];

        public TopicTable()
        {
            var store = new Mock<IThreadStateStore>();
            store.Setup(s => s.SaveTopicAsync(It.IsAny<TopicMetadata>(), It.IsAny<bool>()))
                .Callback((TopicMetadata topic, bool _) => _saved.Add(topic))
                .Returns(Task.CompletedTask);
            store.Setup(s => s.GetTopicByChatIdAndThreadIdAsync(
                    It.IsAny<string>(), It.IsAny<long>(), It.IsAny<long>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((string agentId, long chatId, long threadId, CancellationToken _) =>
                    _saved.FirstOrDefault(t => t.AgentId == agentId && t.ChatId == chatId && t.ThreadId == threadId));
            Store = store.Object;
        }

        public IThreadStateStore Store { get; }

        public IReadOnlyList<TopicMetadata> Saved => _saved;
    }
}