using Domain.Channels;
using Shouldly;
using Telegram.Bot.Types;

namespace Tests.Unit.McpChannelTelegram;

public class TelegramBotServiceTests : IDisposable
{
    private readonly TelegramPollingHarness _harness = new();

    // Inverted when Telegram gained attachments: a photo used to be dropped by the poll loop
    // before anything else looked at it. It now qualifies under the same addressing rule text
    // does, with the caption standing in for the text.
    [Fact]
    public async Task ExecuteAsync_PhotoWithAQualifyingCaption_IsTakenAsATurn()
    {
        var message = TelegramPollingHarness.MediaMessage(caption: "/ask what is this");
        message.Photo = TelegramPollingHarness.Photo();

        await _harness.ReceiveAsync();
        _harness.Enqueue(new Update { Id = 1, Message = message });
        await _harness.RunAsync();

        (await _harness.ReceiveAsync()).Count.ShouldBe(1);
        _harness.Sent.ShouldBeEmpty();
    }

    [Fact]
    public async Task ExecuteAsync_UnauthorizedUser_SendsRejection()
    {
        await _harness.ReceiveAsync();
        _harness.Enqueue(new Update
        {
            Id = 1,
            Message = TelegramPollingHarness.TextMessage("/hello", username: "eve")
        });

        await _harness.RunAsync();

        _harness.Sent.ShouldHaveSingleItem().Text.ShouldBe("You are not authorized to use this bot.");
    }

    [Fact]
    public async Task ExecuteAsync_MessageWithoutSlashOrThread_IsIgnored()
    {
        await _harness.ReceiveAsync();
        _harness.Enqueue(new Update
        {
            Id = 1,
            Message = TelegramPollingHarness.TextMessage("just chatting")
        });

        await _harness.RunAsync();

        _harness.Sent.ShouldBeEmpty();
        (await _harness.ReceiveAsync()).ShouldBeEmpty();
    }

    [Fact]
    public async Task ExecuteAsync_SlashCommand_FromAuthorizedUser_EmitsNotification()
    {
        await _harness.ReceiveAsync();
        _harness.Enqueue(new Update
        {
            Id = 1,
            Message = TelegramPollingHarness.TextMessage("/ask what is 2+2")
        });

        await _harness.RunAsync();

        // No rejection message sent — the message was valid and emitted
        _harness.Sent.ShouldBeEmpty();
        (await _harness.ReceiveAsync()).Count.ShouldBe(1);
    }

    [Fact]
    public async Task ExecuteAsync_CallbackQuery_RoutesToApprovalRouter()
    {
        var (approvalId, resultTask) =
            _harness.CallbackRouter.RegisterApproval(TimeSpan.FromSeconds(10), CancellationToken.None);

        _harness.Enqueue(new Update
        {
            Id = 1,
            CallbackQuery = new CallbackQuery
            {
                Id = "cb-1",
                Data = $"tool_approve:{approvalId}",
                From = new User { Id = 1, IsBot = false, FirstName = "Alice" }
            }
        });

        await _harness.RunAsync();

        (await resultTask).ShouldBe("approved");
    }

    // No subscriber is registered at all here — the cold-start case. Two things are pinned:
    // Telegram stays quiet toward the sender (the reverted drop policy is not allowed to grow
    // back a "the agent is unavailable" reply), and the message is buffered anyway, because the
    // emitter targets the well-known subscriber id and mints its queue on demand.
    [Fact]
    public async Task ExecuteAsync_NoActiveSessions_BuffersSilentlyWithoutRejectingTheSender()
    {
        _harness.Enqueue(new Update { Id = 1, Message = TelegramPollingHarness.TextMessage("/hello") });

        await _harness.RunAsync();

        _harness.Sent.ShouldBeEmpty();
        (await _harness.ReceiveAsync()).ShouldHaveSingleItem().Message!.Content.ShouldBe("/hello");
    }

    // Corrects a regression this suite itself introduced: an earlier round made Telegram gate its
    // emit on a liveness check, so a stale (but not yet evicted)
    // subscriber caused an unconditional drop with only a log line — silent loss to a user actively
    // waiting for a reply. Before that, the same scenario buffered the message and delivered it
    // late on the agent's next reconnect poll (the stable "channel-telegram" subscriber id survives
    // the disconnect). Telegram's own emit path has no way to signal failure back to the sender
    // (unlike ServiceBus's broker-level abandon/redeliver, or Schedule/Library's durable record),
    // so buffering — not dropping — is the correct behavior here: the message must always reach the
    // inbox, regardless of whether anyone is known to be listening right now.
    [Fact]
    public async Task ExecuteAsync_SubscriberWentStaleWithoutRepolling_StillBuffersForALaterPoll()
    {
        await _harness.ReceiveAsync();
        _harness.Time.Advance(ChannelInbox._liveSubscriberFreshness + TimeSpan.FromSeconds(1));

        _harness.Enqueue(new Update
        {
            Id = 1,
            Message = TelegramPollingHarness.TextMessage("/ask what is 2+2")
        });

        await _harness.RunAsync();

        var batch = await _harness.ReceiveAsync();
        batch.Count.ShouldBe(1);
        batch[0].Message!.Content.ShouldBe("/ask what is 2+2");
    }

    [Fact]
    public async Task ExecuteAsync_ValidMessage_RegistersChatAgent()
    {
        await _harness.ReceiveAsync();
        _harness.Enqueue(new Update
        {
            Id = 1,
            Message = TelegramPollingHarness.TextMessage("/ask something")
        });

        await _harness.RunAsync();

        _harness.BotRegistry.GetBotForChat(100).ShouldNotBeNull();
    }

    [Fact]
    public async Task ExecuteAsync_ThreadMessage_IsAccepted()
    {
        var message = TelegramPollingHarness.TextMessage("reply in thread");
        message.MessageThreadId = 42;

        await _harness.ReceiveAsync();
        _harness.Enqueue(new Update { Id = 1, Message = message });

        await _harness.RunAsync();

        // Thread messages are accepted even without / prefix
        _harness.BotRegistry.GetBotForChat(100).ShouldNotBeNull();
    }

    public void Dispose() => _harness.Dispose();
}