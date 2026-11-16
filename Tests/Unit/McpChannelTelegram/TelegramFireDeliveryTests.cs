using Domain.DTOs;
using Domain.DTOs.Channel;
using McpChannelTelegram.McpTools;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol;
using Moq;
using Shouldly;
using Telegram.Bot.Requests;
using Telegram.Bot.Types;

namespace Tests.Unit.McpChannelTelegram;

// A turn nobody started on Telegram — a schedule's fire whose author asked for it there — is delivered
// to its person's private chat with the bot, whose id is their own Telegram id: the conversation is
// that chat's, so the reply goes where the person talks. In pass 20 Bruno's "recuérdame aquí por
// Telegram" schedule fired and its reply went nowhere, since Telegram could open no conversation.
public class TelegramFireDeliveryTests : IDisposable
{
    private static readonly Account _alice = FakeTelegramAccounts.AccountOf("alice").Account;

    private readonly TelegramPollingHarness _harness = new();
    private readonly IServiceProvider _services;

    public TelegramFireDeliveryTests() =>
        _services = new ServiceCollection()
            .AddSingleton(_harness.BotRegistry)
            .AddSingleton(_harness.Access)
            .BuildServiceProvider();

    private Task<string> OpenAsync(Principal? principal, string topicName = "Scheduled task", string? existing = null) =>
        CreateConversationTool.RunAsync(
            TelegramPollingHarness.AgentId, topicName, existing, principal, _services, CancellationToken.None);

    [Fact]
    public async Task AFireForALinkedPerson_OpensTheirPrivateChat_AndItsReplyIsSentThere()
    {
        var conversation = await OpenAsync(new Principal("scheduler", "alice", _alice));

        conversation.ShouldBe($"{TelegramPollingHarness.AliceId}:{TelegramPollingHarness.AliceId}");
        await SendReplyTool.McpRun(conversation, "Bebe agua.", ReplyContentType.Text, isComplete: true, messageId: null,
            new ServiceCollection()
                .AddSingleton(_harness.BotRegistry)
                .AddSingleton(new global::McpChannelTelegram.Services.MessageAccumulator())
                .BuildServiceProvider());
        _harness.Sent.ShouldHaveSingleItem().ChatId.Identifier.ShouldBe(TelegramPollingHarness.AliceId);
    }

    // A bot can write first only in a chat the person opened with it, so each is tried in the order
    // their household prefers until one can; none means there is no conversation to deliver to.
    [Fact]
    public async Task AFireForAPersonNoBotCanWriteTo_OpensNothing()
    {
        _harness.BotClient
            .Setup(b => b.SendRequest(It.IsAny<SendChatActionRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new Telegram.Bot.Exceptions.ApiRequestException("Forbidden: bot can't initiate conversation with a user", 403));

        await Should.ThrowAsync<McpException>(() => OpenAsync(new Principal("scheduler", "alice", _alice)));
    }

    [Fact]
    public async Task AFireForSomebodyWithNoTelegram_OpensNothing()
    {
        await Should.ThrowAsync<McpException>(() =>
            OpenAsync(new Principal("scheduler", "nobody", new Account(FakeTelegramAccounts.Household, "user-nobody"))));
        await Should.ThrowAsync<McpException>(() => OpenAsync(null));
    }

    // The turn-start announce of a later turn in a Telegram conversation has nothing to set up.
    [Fact]
    public async Task AnAnnounceOnAnExistingConversation_ReturnsItAsItIs()
    {
        (await OpenAsync(new Principal("scheduler", "alice", _alice), topicName: string.Empty, existing: "1:1"))
            .ShouldBe("1:1");
        _harness.Sent.ShouldBeEmpty();
    }

    public void Dispose() => _harness.Dispose();
}