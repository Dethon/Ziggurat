using Domain.Contracts;
using Domain.DTOs;
using Domain.DTOs.Channel;
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
}