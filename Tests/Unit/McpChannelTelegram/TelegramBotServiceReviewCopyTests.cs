using Domain.DTOs.Channel;
using Shouldly;
using Telegram.Bot.Types;

namespace Tests.Unit.McpChannelTelegram;

// A person's Telegram conversations sit among their topics in the chat client, so every turn the
// bot hands the agent asks for a review copy. The bot never says where the copy goes nor whose it
// is (ADR 0046): it names the chat, and says whether the conversation is a group's, which is the one
// thing about it the review host cannot work out from the turn.
public class TelegramBotServiceReviewCopyTests : IDisposable
{
    private readonly TelegramPollingHarness _harness = new();

    [Fact]
    public async Task APrivateChatsTurn_AsksForAReviewCopyNamedAfterThePerson()
    {
        await _harness.ReceiveAsync();
        _harness.Enqueue(Update(TelegramPollingHarness.TextMessage("/hello")));

        await _harness.RunAsync();

        var turn = (await _harness.ReceiveAsync()).ShouldHaveSingleItem().Message!;
        turn.ReviewCopy.ShouldBe(new ReviewCopy { Title = "Telegram: alice", Group = false });
        turn.Principal!.Account.ShouldBe(FakeTelegramAccounts.AccountOf("alice").Account);
    }

    [Fact]
    public async Task AGroupsTurn_AsksForAReviewCopyNamedAfterTheGroup_AndSaysItIsAGroups()
    {
        _harness.Accounts.Bind(TelegramPollingHarness.FamilyGroup);
        await _harness.ReceiveAsync();
        _harness.Enqueue(Update(TelegramPollingHarness.GroupMessage("/hello", username: "bob", userId: TelegramPollingHarness.BobId)));

        await _harness.RunAsync();

        var turn = (await _harness.ReceiveAsync()).ShouldHaveSingleItem().Message!;
        turn.ReviewCopy.ShouldBe(new ReviewCopy { Title = "Telegram: Family", Group = true });
        turn.Principal!.Account.ShouldBe(FakeTelegramAccounts.AccountOf("bob").Account);
    }

    private static Update Update(Message message) => new() { Id = 1, Message = message };

    public void Dispose() => _harness.Dispose();
}