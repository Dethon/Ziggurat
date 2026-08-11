using Azure.Messaging.ServiceBus;
using Domain.DTOs;
using McpChannelServiceBus.McpTools;
using McpChannelServiceBus.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using Shouldly;

namespace Tests.Unit.McpChannelServiceBus;

public class SendReplyToolTests
{
    private readonly Mock<ServiceBusSender> _busSender = new();
    private readonly MessageAccumulator _accumulator = new();
    private readonly IServiceProvider _services;

    public SendReplyToolTests()
    {
        _busSender
            .Setup(s => s.SendMessageAsync(It.IsAny<ServiceBusMessage>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var responseSender = new ResponseSender(
            _busSender.Object,
            new Mock<ILogger<ResponseSender>>().Object);

        _services = new ServiceCollection()
            .AddSingleton(_accumulator)
            .AddSingleton(responseSender)
            .BuildServiceProvider();
    }

    [Fact]
    public async Task Run_WithNonTextContentType_ReturnsOkWithoutSending()
    {
        var reasoningResult = await SendReplyTool.McpRun("corr-1", "thinking...", ReplyContentType.Reasoning, false, null, _services);
        var toolCallResult = await SendReplyTool.McpRun("corr-1", "{}", ReplyContentType.ToolCall, false, null, _services);

        reasoningResult.ShouldBe("ok");
        toolCallResult.ShouldBe("ok");
        _busSender.Verify(s => s.SendMessageAsync(
            It.IsAny<ServiceBusMessage>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task McpRun_Error_NoAccumulated_SendsErrorOnly()
    {
        var result = await SendReplyTool.McpRun("corr-1", "something broke", ReplyContentType.Error, false, null, _services);

        result.ShouldBe("ok");
        _busSender.Verify(s => s.SendMessageAsync(
            It.IsAny<ServiceBusMessage>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task McpRun_StreamComplete_FlushesAndSends()
    {
        _accumulator.Append("corr-1", "full response");

        var result = await SendReplyTool.McpRun("corr-1", "", ReplyContentType.StreamComplete, true, null, _services);

        result.ShouldBe("ok");
        _busSender.Verify(s => s.SendMessageAsync(
            It.IsAny<ServiceBusMessage>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task McpRun_StreamComplete_NoAccumulated_DoesNotSend()
    {
        var result = await SendReplyTool.McpRun("corr-1", "", ReplyContentType.StreamComplete, true, null, _services);

        result.ShouldBe("ok");
        _busSender.Verify(s => s.SendMessageAsync(
            It.IsAny<ServiceBusMessage>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task McpRun_TextNotComplete_AccumulatesWithoutSending()
    {
        var result = await SendReplyTool.McpRun("corr-1", "chunk1", ReplyContentType.Text, false, "msg-1", _services);

        result.ShouldBe("ok");
        _busSender.Verify(s => s.SendMessageAsync(
            It.IsAny<ServiceBusMessage>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task McpRun_TextComplete_FlushesAndSends()
    {
        _accumulator.Append("corr-1", "chunk1");

        var result = await SendReplyTool.McpRun("corr-1", "chunk2", ReplyContentType.Text, true, "msg-1", _services);

        result.ShouldBe("ok");
        _busSender.Verify(s => s.SendMessageAsync(
            It.IsAny<ServiceBusMessage>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }
}