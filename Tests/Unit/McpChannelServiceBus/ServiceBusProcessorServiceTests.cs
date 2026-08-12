using System.Text.Json;
using Azure.Messaging.ServiceBus;
using Domain.Channels;
using Domain.DTOs;
using Mcp.Hosting;
using McpChannelServiceBus.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Shouldly;

namespace Tests.Unit.McpChannelServiceBus;

public class ServiceBusProcessorServiceTests : IDisposable
{
    private readonly Mock<ServiceBusProcessor> _processor = new();
    private readonly FakeTimeProvider _time = new();
    private readonly ChannelInbox _inbox;
    private readonly ChannelNotificationEmitter _emitter;
    private readonly ServiceBusProcessorService _sut;
    private readonly CancellationTokenSource _cts = new();

    public ServiceBusProcessorServiceTests()
    {
        _inbox = new ChannelInbox(_time);
        _emitter = new ChannelNotificationEmitter(_inbox, DeliveryPolicy.GateOnLive);

        _processor
            .Setup(p => p.StartProcessingAsync(It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        _processor
            .Setup(p => p.StopProcessingAsync(It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        _sut = new ServiceBusProcessorService(
            _processor.Object,
            _emitter,
            new Mock<ILogger<ServiceBusProcessorService>>().Object);
    }

    [Fact]
    public async Task ExecuteAsync_StartsAndStopsProcessor()
    {
        await StartAndStopService();

        _processor.Verify(p => p.StartProcessingAsync(It.IsAny<CancellationToken>()), Times.Once);
        _processor.Verify(p => p.StopProcessingAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task ProcessMessage_MissingOrEmptyPrompt_DeadLettersMessage(string? prompt)
    {
        await _inbox.ReceiveAsync("sess-1", TimeSpan.Zero, CancellationToken.None);

        var receiver = new Mock<ServiceBusReceiver>();
        receiver
            .Setup(r => r.DeadLetterMessageAsync(
                It.IsAny<ServiceBusReceivedMessage>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var message = CreateReceivedMessage(new ServiceBusPromptMessage
        {
            CorrelationId = "corr-1",
            Prompt = prompt
        });

        var args = new ProcessMessageEventArgs(message, receiver.Object, CancellationToken.None);
        await _sut.ProcessMessageAsync(args);

        receiver.Verify(r => r.DeadLetterMessageAsync(
            It.IsAny<ServiceBusReceivedMessage>(),
            "InvalidMessage",
            "Missing required fields",
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ProcessMessage_NoActiveSessions_AbandonsMessage()
    {
        var receiver = new Mock<ServiceBusReceiver>();
        receiver
            .Setup(r => r.AbandonMessageAsync(
                It.IsAny<ServiceBusReceivedMessage>(),
                It.IsAny<IDictionary<string, object>>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var message = CreateReceivedMessage(new ServiceBusPromptMessage
        {
            CorrelationId = "corr-1",
            Prompt = "Hello"
        });

        var args = new ProcessMessageEventArgs(message, receiver.Object, CancellationToken.None);
        await _sut.ProcessMessageAsync(args);

        receiver.Verify(r => r.AbandonMessageAsync(
            It.IsAny<ServiceBusReceivedMessage>(),
            It.IsAny<IDictionary<string, object>>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    // Regression: a subscriber that registered once and then went quiet (the agent's channel
    // connection dropped) is not evicted by PruneIdle until it has been both empty and idle for a
    // full hour, so a liveness check that only asked whether a subscriber existed stayed true long
    // after nobody was actually polling. That let this settle (complete) the broker message
    // instead of abandoning it — defeating Service Bus's
    // at-least-once redelivery, since the buffered item can still be lost with the in-process inbox.
    [Fact]
    public async Task ProcessMessage_SubscriberWentStaleWithoutRepolling_AbandonsMessage()
    {
        await _inbox.ReceiveAsync("sess-1", TimeSpan.Zero, CancellationToken.None);
        _time.Advance(ChannelInbox._liveSubscriberFreshness + TimeSpan.FromSeconds(1));

        var receiver = new Mock<ServiceBusReceiver>();
        receiver
            .Setup(r => r.AbandonMessageAsync(
                It.IsAny<ServiceBusReceivedMessage>(),
                It.IsAny<IDictionary<string, object>>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var message = CreateReceivedMessage(new ServiceBusPromptMessage
        {
            CorrelationId = "corr-1",
            Prompt = "Hello"
        });

        var args = new ProcessMessageEventArgs(message, receiver.Object, CancellationToken.None);
        await _sut.ProcessMessageAsync(args);

        receiver.Verify(r => r.AbandonMessageAsync(
            It.IsAny<ServiceBusReceivedMessage>(),
            It.IsAny<IDictionary<string, object>>(),
            It.IsAny<CancellationToken>()), Times.Once);
        receiver.Verify(r => r.CompleteMessageAsync(
            It.IsAny<ServiceBusReceivedMessage>(),
            It.IsAny<CancellationToken>()), Times.Never);
        (await _inbox.ReceiveAsync("sess-1", TimeSpan.Zero, CancellationToken.None)).ShouldBeEmpty();
    }

    // The mirror of the case above: once the emit has handed the prompt to a live subscriber, a
    // failed settle must not abandon. Abandoning gives the same prompt back to the broker and
    // redelivery replays it to the agent — the duplicate gate-on-live exists to prevent.
    [Fact]
    public async Task ProcessMessage_TheSettleFailsAfterADeliveredEmit_DoesNotAbandonTheMessage()
    {
        await _inbox.ReceiveAsync("sess-1", TimeSpan.Zero, CancellationToken.None);

        var receiver = new Mock<ServiceBusReceiver>();
        receiver
            .Setup(r => r.CompleteMessageAsync(It.IsAny<ServiceBusReceivedMessage>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new ServiceBusException("lock lost", ServiceBusFailureReason.MessageLockLost));
        receiver
            .Setup(r => r.AbandonMessageAsync(
                It.IsAny<ServiceBusReceivedMessage>(),
                It.IsAny<IDictionary<string, object>>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var message = CreateReceivedMessage(new ServiceBusPromptMessage
        {
            CorrelationId = "corr-1",
            Prompt = "Hello"
        });

        var args = new ProcessMessageEventArgs(message, receiver.Object, CancellationToken.None);
        await _sut.ProcessMessageAsync(args);

        receiver.Verify(r => r.AbandonMessageAsync(
            It.IsAny<ServiceBusReceivedMessage>(),
            It.IsAny<IDictionary<string, object>>(),
            It.IsAny<CancellationToken>()), Times.Never);
        (await _inbox.ReceiveAsync("sess-1", TimeSpan.Zero, CancellationToken.None)).ShouldHaveSingleItem();
    }

    [Fact]
    public async Task ProcessMessage_NullCorrelationId_EmitsGeneratedConversationId()
    {
        await _inbox.ReceiveAsync("sess-1", TimeSpan.Zero, CancellationToken.None);

        var receiver = new Mock<ServiceBusReceiver>();
        receiver
            .Setup(r => r.CompleteMessageAsync(It.IsAny<ServiceBusReceivedMessage>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var message = CreateReceivedMessage(new ServiceBusPromptMessage
        {
            CorrelationId = null,
            Prompt = "Hello"
        });

        var args = new ProcessMessageEventArgs(message, receiver.Object, CancellationToken.None);
        await _sut.ProcessMessageAsync(args);

        // Neither the payload nor the broker message carries a correlation id, so the service
        // must mint a guid for the conversation rather than emit an empty one.
        var notification = (await _inbox.ReceiveAsync("sess-1", TimeSpan.Zero, CancellationToken.None))
            .ShouldHaveSingleItem().Message.ShouldNotBeNull();
        Guid.TryParse(notification.ConversationId, out _).ShouldBeTrue();
        notification.Content.ShouldBe("Hello");
        receiver.Verify(r => r.CompleteMessageAsync(
            It.IsAny<ServiceBusReceivedMessage>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ProcessMessage_NullSender_EmitsServiceBusDefaultSender()
    {
        await _inbox.ReceiveAsync("sess-1", TimeSpan.Zero, CancellationToken.None);

        var receiver = new Mock<ServiceBusReceiver>();
        receiver
            .Setup(r => r.CompleteMessageAsync(It.IsAny<ServiceBusReceivedMessage>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var message = CreateReceivedMessage(new ServiceBusPromptMessage
        {
            CorrelationId = "corr-1",
            Prompt = "Hello",
            Sender = null
        });

        var args = new ProcessMessageEventArgs(message, receiver.Object, CancellationToken.None);
        await _sut.ProcessMessageAsync(args);

        var notification = (await _inbox.ReceiveAsync("sess-1", TimeSpan.Zero, CancellationToken.None))
            .ShouldHaveSingleItem().Message.ShouldNotBeNull();
        notification.Sender.ShouldBe("service-bus");
        notification.ConversationId.ShouldBe("corr-1");
        receiver.Verify(r => r.CompleteMessageAsync(
            It.IsAny<ServiceBusReceivedMessage>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    private async Task StartAndStopService()
    {
        _cts.CancelAfter(TimeSpan.FromMilliseconds(100));
        await _sut.StartAsync(_cts.Token);
        await Task.Delay(50, CancellationToken.None);
        await _sut.StopAsync(CancellationToken.None);
    }

    private static ServiceBusReceivedMessage CreateReceivedMessage(ServiceBusPromptMessage prompt)
    {
        var json = JsonSerializer.Serialize(prompt);
        return ServiceBusModelFactory.ServiceBusReceivedMessage(
            body: BinaryData.FromString(json),
            messageId: Guid.NewGuid().ToString());
    }

    public void Dispose() => _cts.Dispose();
}