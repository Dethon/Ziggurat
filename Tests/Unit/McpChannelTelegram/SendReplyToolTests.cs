using Domain.DTOs;
using McpChannelTelegram.McpTools;
using McpChannelTelegram.Services;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Shouldly;
using Telegram.Bot;
using Telegram.Bot.Requests;

namespace Tests.Unit.McpChannelTelegram;

public class SendReplyToolTests
{
    private readonly Mock<ITelegramBotClient> _botClient = new();
    private readonly MessageAccumulator _accumulator = new();
    private readonly IServiceProvider _services;

    public SendReplyToolTests()
    {
        var botRegistry = new BotRegistry(new Dictionary<string, ITelegramBotClient>
        {
            ["jack"] = _botClient.Object
        });
        botRegistry.RegisterChatAgent(100, "jack");

        _services = new ServiceCollection()
            .AddSingleton(botRegistry)
            .AddSingleton(_accumulator)
            .BuildServiceProvider();
    }

    [Fact]
    public async Task Run_WithNonTextContentType_ReturnsOkWithoutSending()
    {
        var reasoningResult = await SendReplyTool.McpRun("100:100", "thinking...", ReplyContentType.Reasoning, false, null, _services);
        var toolCallResult = await SendReplyTool.McpRun("100:100", """{"Name":"mcp__server__search","Arguments":{"query":"test"}}""", ReplyContentType.ToolCall, false, null, _services);

        reasoningResult.ShouldBe("ok");
        toolCallResult.ShouldBe("ok");
        _botClient.Verify(b => b.SendRequest(
            It.IsAny<SendMessageRequest>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task McpRun_Error_SendsErrorAndFlushesAccumulated()
    {
        _accumulator.Append("100:100", "partial text");

        var result = await SendReplyTool.McpRun("100:100", "something broke", ReplyContentType.Error, false, null, _services);

        result.ShouldBe("ok");
        // Two sends: one for accumulated text, one for error
        _botClient.Verify(b => b.SendRequest(
            It.IsAny<SendMessageRequest>(),
            It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task McpRun_StreamComplete_FlushesAccumulatedText()
    {
        _accumulator.Append("100:100", "full response");

        var result = await SendReplyTool.McpRun("100:100", "", ReplyContentType.StreamComplete, true, null, _services);

        result.ShouldBe("ok");
        _botClient.Verify(b => b.SendRequest(
            It.IsAny<SendMessageRequest>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task McpRun_TextNotComplete_AccumulatesWithoutSending()
    {
        var result = await SendReplyTool.McpRun("100:100", "chunk1", ReplyContentType.Text, false, "msg-1", _services);

        result.ShouldBe("ok");
        _botClient.Verify(b => b.SendRequest(
            It.IsAny<SendMessageRequest>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task McpRun_TextComplete_FlushesAndSends()
    {
        _accumulator.Append("100:100", "chunk1");

        var result = await SendReplyTool.McpRun("100:100", "chunk2", ReplyContentType.Text, true, "msg-1", _services);

        result.ShouldBe("ok");
        _botClient.Verify(b => b.SendRequest(
            It.IsAny<SendMessageRequest>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task McpRun_UnknownChat_ThrowsInvalidOperation()
    {
        await Should.ThrowAsync<InvalidOperationException>(
            () => SendReplyTool.McpRun("999:999", "hello", ReplyContentType.Text, true, null, _services));
    }

    // A Telegram tool should never receive another channel's address; when it does, the error
    // names the value instead of an unguarded parse throwing whatever it throws.
    [Fact]
    public async Task McpRun_SomeOtherChannelsAddress_IsRefusedNamingTheValue()
    {
        var ex = await Should.ThrowAsync<InvalidOperationException>(
            () => SendReplyTool.McpRun("kitchen-satellite", "hello", ReplyContentType.Text, true, null, _services));

        ex.Message.ShouldContain("kitchen-satellite");
        ex.Message.ShouldContain("conversation identity");
    }

    // The forum case: an unequal thread rides along to the Telegram API, narrowed to its int.
    [Fact]
    public async Task McpRun_ForumThread_SendsIntoTheThread()
    {
        _accumulator.Append("100:42", "hola");

        var result = await SendReplyTool.McpRun("100:42", "", ReplyContentType.StreamComplete, true, null, _services);

        result.ShouldBe("ok");
        _botClient.Verify(b => b.SendRequest(
            It.Is<SendMessageRequest>(r => r.MessageThreadId == 42),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task McpRun_StreamComplete_NoAccumulated_DoesNotSend()
    {
        var result = await SendReplyTool.McpRun("100:100", "", ReplyContentType.StreamComplete, true, null, _services);

        result.ShouldBe("ok");
        _botClient.Verify(b => b.SendRequest(
            It.IsAny<SendMessageRequest>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }
}