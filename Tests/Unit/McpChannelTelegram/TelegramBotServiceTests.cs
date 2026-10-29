using Domain.Channels;
using Shouldly;
using Telegram.Bot.Types;

namespace Tests.Unit.McpChannelTelegram;

public class TelegramBotServiceTests : IDisposable
{
    private readonly TelegramPollingHarness _harness = new();

    [Fact]
    public async Task ExecuteAsync_UnauthorizedUser_SendsRejection()
    {
        await _harness.ReceiveAsync();
        _harness.Enqueue(new Update
        {
            Id = 1,
            Message = TelegramPollingHarness.TextMessage("/hello", username: "eve", userId: 3)
        });

        await _harness.RunAsync();

        _harness.Sent.ShouldHaveSingleItem().Text.ShouldBe("You are not authorized to use this bot.");
    }

    // A username is its owner's to change and Telegram's to reassign, so a name that matches is no
    // longer anybody: only the listed number is.
    [Fact]
    public async Task ASenderWhoseIdIsNotListed_IsRefused_EvenUnderAListedPersonsNames()
    {
        using var harness = new TelegramPollingHarness(TelegramPollingHarness.AliceId);
        await harness.ReceiveAsync();
        harness.Enqueue(new Update
        {
            Id = 1,
            Message = TelegramPollingHarness.TextMessage("/hello", username: "alice", userId: 99)
        });

        await harness.RunAsync();

        harness.Sent.ShouldHaveSingleItem().Text.ShouldBe("You are not authorized to use this bot.");
        (await harness.ReceiveAsync()).ShouldBeEmpty();
    }

    [Fact]
    public async Task AListedId_IsAdmitted_AndTheTurnsSenderIsTheirUsername()
    {
        await _harness.ReceiveAsync();
        _harness.Enqueue(new Update
        {
            Id = 1,
            Message = TelegramPollingHarness.TextMessage("/hello", username: "bob", userId: TelegramPollingHarness.BobId)
        });

        await _harness.RunAsync();

        _harness.Sent.ShouldBeEmpty();
        (await _harness.ReceiveAsync()).ShouldHaveSingleItem().Message!.Sender.ShouldBe("bob");
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
    public async Task ExecuteAsync_CallbackQuery_RoutesToApprovalRouter()
    {
        var (approvalId, resultTask) =
            _harness.CallbackRouter.RegisterApproval(
                TimeSpan.FromSeconds(10), new HashSet<long> { 1 }, CancellationToken.None);

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

    public void Dispose() => _harness.Dispose();
}