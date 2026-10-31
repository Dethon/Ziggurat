using Domain.DTOs.Channel;
using McpChannelTelegram.McpTools;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Shouldly;
using Telegram.Bot.Requests;

namespace Tests.Unit.McpChannelTelegram;

// A timer with no satellite rings on its person's Telegram too: a message in their private chat with
// the bot — whose id is their own Telegram id — with one Dismiss button. Their tap dismisses it
// everywhere; anyone else's is refused; an alert that ended elsewhere says so.
public class TelegramAlertOfferTests : IDisposable
{
    private static readonly Account _alice = FakeTelegramAccounts.AccountOf("alice").Account;

    private readonly TelegramPollingHarness _harness = new();
    private readonly IServiceProvider _services;

    public TelegramAlertOfferTests() =>
        _services = new ServiceCollection()
            .AddSingleton(_harness.BotRegistry)
            .AddSingleton(_harness.CallbackRouter)
            .AddSingleton(_harness.Access)
            .BuildServiceProvider();

    private Task<string> OfferAsync(string alertId, Account owner, string agentId = TelegramPollingHarness.AgentId) =>
        OfferAlertTool.RunAsync(new OfferAlertParams
        {
            AlertId = alertId, Owner = owner, Text = "pasta is ready", AgentId = agentId
        }, _services, CancellationToken.None);

    [Fact]
    public async Task AnOfferedAlert_IsSentToItsOwnersPrivateChat_AndDismissedByTheirTapAlone()
    {
        var offered = OfferAsync(Guid.NewGuid().ToString("N"), _alice);

        await Eventually.Until(() => _harness.Sent.Count == 1, "the alert to be sent");
        var sent = _harness.Sent.ShouldHaveSingleItem();
        sent.ChatId.Identifier.ShouldBe(TelegramPollingHarness.AliceId);
        sent.Text.ShouldContain("pasta is ready");
        await _harness.TapAsync(TelegramPollingHarness.BobId, "alert_dismiss");
        await Eventually.Settle();
        offered.IsCompleted.ShouldBeFalse();
        _harness.BotClient.Verify(b => b.SendRequest(
            It.Is<AnswerCallbackQueryRequest>(r => r.Text == "This alert isn't yours"),
            It.IsAny<CancellationToken>()), Times.Once);

        await _harness.TapAsync(TelegramPollingHarness.AliceId, "alert_dismiss");

        (await offered.WaitAsync(TimeSpan.FromSeconds(5))).ShouldBe(ChannelProtocol.AlertDismissed);
        _harness.BotClient.Verify(b => b.SendRequest(
            It.Is<EditMessageTextRequest>(r => (r.Text ?? "").Contains("pasta is ready") && (r.Text ?? "").Contains("dismissed")),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task AnAlertForSomeoneWithNoTelegram_IsNoAnswerAtOnce()
    {
        var offered = await OfferAsync(
            Guid.NewGuid().ToString("N"), new Account(FakeTelegramAccounts.Household, "user-nobody"));

        offered.ShouldBe(ChannelProtocol.NoAnswer);
        _harness.Sent.ShouldBeEmpty();
    }

    // Dismissed in the chat client first, or rung out, the message says what became of it and asks
    // nothing more.
    [Theory]
    [InlineData(true, "dismissed")]
    [InlineData(false, "stopped ringing")]
    public async Task AnAlertThatEndedElsewhere_SaysSoAndIsNoAnswerHere(bool dismissed, string said)
    {
        var alertId = Guid.NewGuid().ToString("N");
        var offered = OfferAsync(alertId, _alice);
        await Eventually.Until(() => _harness.Sent.Count == 1, "the alert to be sent");

        await SettleAlertTool.RunAsync(alertId, dismissed, _services, CancellationToken.None);

        (await offered.WaitAsync(TimeSpan.FromSeconds(5))).ShouldBe(ChannelProtocol.NoAnswer);
        _harness.BotClient.Verify(b => b.SendRequest(
            It.Is<EditMessageTextRequest>(r => (r.Text ?? "").Contains("pasta is ready")
                                               && (r.Text ?? "").Contains(said)
                                               && r.ChatId.Identifier == TelegramPollingHarness.AliceId),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    // A timer set through an agent with no bot of its own still reaches the person, by a bot that has.
    [Fact]
    public async Task AnAlertSetThroughAnAgentWithNoBot_IsSentByAnotherBot()
    {
        _ = OfferAsync(Guid.NewGuid().ToString("N"), _alice, agentId: "nabu");

        await Eventually.Until(() => _harness.Sent.Count == 1, "the alert to be sent");
        _harness.Sent.ShouldHaveSingleItem().ChatId.Identifier.ShouldBe(TelegramPollingHarness.AliceId);
    }

    public void Dispose() => _harness.Dispose();
}