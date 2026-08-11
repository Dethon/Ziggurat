using Domain.Contracts;
using Domain.DTOs;
using Domain.DTOs.Channel;
using Domain.DTOs.WebChat;
using McpChannelSignalR.Services;
using Microsoft.Extensions.Logging;
using Moq;
using Shouldly;

namespace Tests.Unit.McpChannelSignalR;

public class StreamServiceTests : IDisposable
{
    private readonly SessionService _sessionService = new();
    private readonly Mock<IPushNotificationService> _pushNotification = new();
    private readonly StreamService _sut;

    public StreamServiceTests()
    {
        _sut = new StreamService(
            _sessionService,
            _pushNotification.Object,
            new Mock<ILogger<StreamService>>().Object);
    }

    [Fact]
    public void GetOrCreateStream_CreatesAndReusesStream()
    {
        var (channel1, _) = _sut.GetOrCreateStream("topic1", "prompt", "user1", CancellationToken.None);
        channel1.ShouldNotBeNull();
        _sut.IsStreaming("topic1").ShouldBeTrue();

        var (channel2, _) = _sut.GetOrCreateStream("topic1", "p2", "u1", CancellationToken.None);
        channel1.ShouldBeSameAs(channel2);
    }

    [Fact]
    public async Task WriteReplyAsync_TextContent_CreatesCorrectMessage()
    {
        _sessionService.StartSession("topic1", "agent1", 100, 200);
        var (channel, _) = _sut.GetOrCreateStream("topic1", "prompt", "user1", CancellationToken.None);
        var reader = channel.Subscribe();

        await _sut.WriteReplyAsync(new SendReplyParams
        { ConversationId = "100:200", Content = "hello", ContentType = ReplyContentType.Text, IsComplete = false, MessageId = "msg-1" });

        var msg = await reader.ReadAsync();
        msg.Content.ShouldBe("hello");
        msg.MessageId.ShouldBe("msg-1");
    }

    [Fact]
    public async Task WriteReplyAsync_StreamComplete_CompletesStream()
    {
        _sessionService.StartSession("topic1", "agent1", 100, 200);
        _sut.GetOrCreateStream("topic1", "prompt", "user1", CancellationToken.None);

        await _sut.WriteReplyAsync(new SendReplyParams
        { ConversationId = "100:200", Content = "", ContentType = ReplyContentType.StreamComplete, IsComplete = true });

        _sut.IsStreaming("topic1").ShouldBeFalse();
    }

    [Fact]
    public void CancelStream_CancelsTokenAndCleansUp()
    {
        var (_, token) = _sut.GetOrCreateStream("topic1", "prompt", "user1", CancellationToken.None);

        _sut.CancelStream("topic1");

        token.IsCancellationRequested.ShouldBeTrue();
        _sut.IsStreaming("topic1").ShouldBeFalse();
    }

    [Fact]
    public void TryIncrementPending_ReturnsBasedOnStreamExistence()
    {
        _sut.TryIncrementPending("nonexistent").ShouldBeFalse();

        _sut.GetOrCreateStream("topic1", "prompt", "user1", CancellationToken.None);
        _sut.TryIncrementPending("topic1").ShouldBeTrue();
    }

    [Fact]
    public void GetStreamState_ActiveStream_ReturnsState()
    {
        _sut.GetOrCreateStream("topic1", "my prompt", "user1", CancellationToken.None);

        var state = _sut.GetStreamState("topic1");
        state.ShouldNotBeNull();
        state.IsProcessing.ShouldBeTrue();
        state.CurrentPrompt.ShouldBe("my prompt");
    }

    [Fact]
    public void GetStreamState_NoStream_ReturnsNull()
    {
        _sut.GetStreamState("nonexistent").ShouldBeNull();
    }

    [Fact]
    public async Task WriteReplyAsync_StreamComplete_SendsPushNotificationWithTopicName()
    {
        _sessionService.StartSession("topic1", "agent1", 100, 200, spaceSlug: "myspace", topicName: "Apartment search");
        _sut.GetOrCreateStream("topic1", "prompt", "user1", CancellationToken.None);

        await _sut.WriteReplyAsync(new SendReplyParams
        { ConversationId = "100:200", Content = "", ContentType = ReplyContentType.StreamComplete, IsComplete = true });

        // Allow fire-and-forget to complete
        await Task.Delay(100);

        _pushNotification.Verify(p => p.SendToSpaceAsync(
            "myspace",
            "Apartment search",
            "The agent has finished responding",
            "/myspace",
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task WriteReplyAsync_StreamComplete_FallsBackToDefaultTitleWhenNoTopicName()
    {
        _sessionService.StartSession("topic1", "agent1", 100, 200, spaceSlug: "myspace");
        _sut.GetOrCreateStream("topic1", "prompt", "user1", CancellationToken.None);

        await _sut.WriteReplyAsync(new SendReplyParams
        { ConversationId = "100:200", Content = "", ContentType = ReplyContentType.StreamComplete, IsComplete = true });

        // Allow fire-and-forget to complete
        await Task.Delay(100);

        _pushNotification.Verify(p => p.SendToSpaceAsync(
            "myspace",
            "New response",
            "The agent has finished responding",
            "/myspace",
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task WriteReplyAsync_FirstAgentCompletes_StreamStaysAliveForSecondAgent()
    {
        // Arrange: simulate two messages on the same topic (message1 then enqueued message2)
        _sessionService.StartSession("topic1", "agent1", 100, 200);
        var (channel, _) = _sut.GetOrCreateStream("topic1", "prompt1", "user1", CancellationToken.None);
        _sut.TryIncrementPending("topic1"); // message 1
        _sut.TryIncrementPending("topic1"); // message 2 (enqueued)

        var reader = channel.Subscribe();

        // Act: first agent completes its response
        await _sut.WriteReplyAsync(new SendReplyParams
        {
            ConversationId = "100:200", Content = "done with download 1",
            ContentType = ReplyContentType.Text, IsComplete = true, MessageId = "msg-1"
        });

        // Assert: stream should still be alive for second agent
        _sut.IsStreaming("topic1").ShouldBeTrue();

        await _sut.WriteReplyAsync(new SendReplyParams
        {
            ConversationId = "100:200", Content = "starting download 2",
            ContentType = ReplyContentType.Text, IsComplete = false, MessageId = "msg-2"
        });

        var msg1 = await reader.ReadAsync();
        msg1.Content.ShouldBe("done with download 1");

        var complete1 = await reader.ReadAsync();
        complete1.IsComplete.ShouldBeTrue();

        var msg2 = await reader.ReadAsync();
        msg2.Content.ShouldBe("starting download 2");
    }

    [Fact]
    public async Task WriteReplyAsync_LastAgentCompletes_StreamTornDown()
    {
        // Arrange: two pending messages
        _sessionService.StartSession("topic1", "agent1", 100, 200);
        _sut.GetOrCreateStream("topic1", "prompt1", "user1", CancellationToken.None);
        _sut.TryIncrementPending("topic1"); // message 1
        _sut.TryIncrementPending("topic1"); // message 2

        // Act: both agents complete
        await _sut.WriteReplyAsync(new SendReplyParams
        {
            ConversationId = "100:200", Content = "", ContentType = ReplyContentType.StreamComplete,
            IsComplete = true, MessageId = "msg-1"
        });
        await _sut.WriteReplyAsync(new SendReplyParams
        {
            ConversationId = "100:200", Content = "", ContentType = ReplyContentType.StreamComplete,
            IsComplete = true, MessageId = "msg-2"
        });

        // Assert: stream should be torn down after last agent completes
        _sut.IsStreaming("topic1").ShouldBeFalse();
    }

    public void Dispose() => _sut.Dispose();
}