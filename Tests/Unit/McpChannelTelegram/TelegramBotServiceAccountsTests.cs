using Domain.DTOs.Channel;
using Domain.Security;
using Domain.Security.Signing;
using Shouldly;
using Telegram.Bot.Types;

namespace Tests.Unit.McpChannelTelegram;

// The bot knows a person by the numeric Telegram id linked to their account, and nobody else: an
// invite or the account page's code, sent as the start parameter, links the Telegram that sends it;
// a linked person asks for a link onto the web; an owner's code binds a group; and every turn the
// bot hands the agent acts for the sender's account.
public class TelegramBotServiceAccountsTests : IDisposable
{
    private const long Carol = 3;

    private readonly TelegramPollingHarness _harness = new();

    [Fact]
    public async Task ALinkedSendersTurn_ActsForTheirAccount()
    {
        await _harness.ReceiveAsync();
        _harness.Enqueue(Update(TelegramPollingHarness.TextMessage("/hello", username: "bob", userId: TelegramPollingHarness.BobId)));

        await _harness.RunAsync();

        _harness.Sent.ShouldBeEmpty();
        var turn = (await _harness.ReceiveAsync()).ShouldHaveSingleItem().Message!;
        turn.Sender.ShouldBe("bob");
        turn.Principal.ShouldBe(new Principal("telegram", "bob", FakeTelegramAccounts.AccountOf("bob").Account));
    }

    // The agent believes the accounts service's word, not the bot's: the turn carries the assertion
    // the service answered with when it resolved the sender's linked number.
    [Fact]
    public async Task ALinkedSendersTurn_CarriesTheAccountsServicesAssertionOfThem()
    {
        await _harness.ReceiveAsync();
        _harness.Enqueue(Update(TelegramPollingHarness.TextMessage("/hello", username: "bob", userId: TelegramPollingHarness.BobId)));

        await _harness.RunAsync();

        var turn = (await _harness.ReceiveAsync()).ShouldHaveSingleItem().Message!;
        (await VerifiedAsync(turn.Assertion)).Account.ShouldBe(FakeTelegramAccounts.AccountOf("bob").Account);
    }

    // The person an invite made is let in on the very next message, on the redemption's own answer.
    [Fact]
    public async Task AnInvitedPersonsFirstTurn_CarriesAnAssertionOfThem()
    {
        var invite = _harness.Accounts.Invite("Carol");
        await _harness.ReceiveAsync();
        _harness.Enqueue(
            Update(TelegramPollingHarness.TextMessage($"/start {invite}", username: "carol", userId: Carol)),
            Update(TelegramPollingHarness.TextMessage("/what time is it", username: "carol", userId: Carol)));

        await _harness.RunAsync();

        var turn = (await _harness.ReceiveAsync()).ShouldHaveSingleItem().Message!;
        (await VerifiedAsync(turn.Assertion)).Account.ShouldBe(FakeTelegramAccounts.AccountOf("Carol").Account);
    }

    private static async Task<IdentityClaims> VerifiedAsync(string? assertion) =>
        (await TestAssertions.Verifier().VerifyTurnAsync(assertion, "telegram", CancellationToken.None))
        .ShouldBeOfType<TurnAdmission.Admitted>().Identity;

    // A username is its owner's to change and Telegram's to reassign, so only the linked number counts.
    [Fact]
    public async Task AnUnlinkedSender_IsRefusedWithAHintToAskForAnInvite()
    {
        await _harness.ReceiveAsync();
        _harness.Enqueue(Update(TelegramPollingHarness.TextMessage("/hello", username: "alice", userId: 99)));

        await _harness.RunAsync();

        _harness.Sent.ShouldHaveSingleItem().Text.ShouldContain("ask an owner of the household for an invite");
        (await _harness.ReceiveAsync()).ShouldBeEmpty();
    }

    [Fact]
    public async Task AnInviteAsTheStartParameter_WelcomesThePersonAndTheirNextMessageIsTheirTurn()
    {
        var invite = _harness.Accounts.Invite("Carol");
        await _harness.ReceiveAsync();
        _harness.Enqueue(
            Update(TelegramPollingHarness.TextMessage("/hi", username: "carol", userId: Carol)),
            Update(TelegramPollingHarness.TextMessage($"/start {invite}", username: "carol", userId: Carol)),
            Update(TelegramPollingHarness.TextMessage("/what time is it", username: "carol", userId: Carol)));

        await _harness.RunAsync();

        _harness.Sent.Count.ShouldBe(2);
        _harness.Sent[1].Text.ShouldContain("Welcome, Carol");
        var turn = (await _harness.ReceiveAsync()).ShouldHaveSingleItem().Message!;
        turn.Content.ShouldBe("/what time is it");
        turn.Principal!.Account.ShouldBe(FakeTelegramAccounts.AccountOf("Carol").Account);
    }

    [Fact]
    public async Task TheAccountPagesCodeAsTheStartParameter_LinksTheTelegram()
    {
        var code = _harness.Accounts.LinkCode("Carol");
        await _harness.ReceiveAsync();
        _harness.Enqueue(Update(TelegramPollingHarness.TextMessage($"/start {code}", username: "carol", userId: Carol)));

        await _harness.RunAsync();

        _harness.Sent.ShouldHaveSingleItem().Text.ShouldContain("Linked");
        (await _harness.ReceiveAsync()).ShouldBeEmpty("redeeming a code is not a turn");
    }

    [Fact]
    public async Task ACodeUsedAlready_IsRefusedAndAdmitsNobody()
    {
        var invite = _harness.Accounts.Invite("Carol");
        await _harness.Accounts.RedeemStartAsync(Carol, invite, CancellationToken.None);
        await _harness.ReceiveAsync();
        _harness.Enqueue(
            Update(TelegramPollingHarness.TextMessage($"/start {invite}", username: "dave", userId: 4)),
            Update(TelegramPollingHarness.TextMessage("/hello", username: "dave", userId: 4)));

        await _harness.RunAsync();

        _harness.Sent[0].Text.ShouldContain("can't be used");
        _harness.Sent[1].Text.ShouldContain("ask an owner of the household for an invite");
        (await _harness.ReceiveAsync()).ShouldBeEmpty();
    }

    [Fact]
    public async Task AnInviteFromATelegramAlreadyLinked_IsRefused()
    {
        var invite = _harness.Accounts.Invite("Someone new");
        _harness.Enqueue(Update(TelegramPollingHarness.TextMessage($"/start {invite}")));

        await _harness.RunAsync();

        _harness.Sent.ShouldHaveSingleItem().Text.ShouldContain("already");
    }

    // A bare /start is what Telegram sends when someone first opens the bot: to a stranger it is the
    // hint, to a linked person a turn as before.
    [Fact]
    public async Task ABareStartFromAStranger_IsTheHint()
    {
        _harness.Enqueue(Update(TelegramPollingHarness.TextMessage("/start", username: "carol", userId: Carol)));

        await _harness.RunAsync();

        _harness.Sent.ShouldHaveSingleItem().Text.ShouldContain("ask an owner of the household for an invite");
    }

    [Fact]
    public async Task WebInAPrivateChat_AnswersALinkedPersonALinkThatAddsAPasskey()
    {
        _harness.Enqueue(Update(TelegramPollingHarness.TextMessage("/web")));

        await _harness.RunAsync();

        var answer = _harness.Sent.ShouldHaveSingleItem().Text;
        answer.ShouldContain("https://assistants.example/accounts/enroll/");
        answer.ShouldContain("passkey");
        (await _harness.ReceiveAsync()).ShouldBeEmpty();
    }

    [Fact]
    public async Task WebFromAnUnlinkedPerson_MintsNothing()
    {
        _harness.Enqueue(Update(TelegramPollingHarness.TextMessage("/web", username: "carol", userId: Carol)));

        await _harness.RunAsync();

        _harness.Sent.ShouldHaveSingleItem().Text.ShouldContain("ask an owner of the household for an invite");
        _harness.Accounts.WebLinksMinted.ShouldBe(0);
    }

    // Whoever opens the link gets into the account, so it never goes where others read it.
    [Fact]
    public async Task WebInAGroup_SendsNoLink()
    {
        _harness.Accounts.Bind(TelegramPollingHarness.FamilyGroup);
        _harness.Enqueue(Update(TelegramPollingHarness.GroupMessage("/web")));

        await _harness.RunAsync();

        _harness.Sent.ShouldHaveSingleItem().Text.ShouldContain("private chat");
        _harness.Accounts.WebLinksMinted.ShouldBe(0);
    }

    [Fact]
    public async Task WebForTheOperator_SendsNoLink()
    {
        _harness.Accounts.MakeOperator(TelegramPollingHarness.AliceId);
        _harness.Enqueue(Update(TelegramPollingHarness.TextMessage("/web")));

        await _harness.RunAsync();

        _harness.Sent.ShouldHaveSingleItem().Text.ShouldNotContain("https://");
        _harness.Accounts.WebLinksMinted.ShouldBe(0);
    }

    // A command meant as words is a turn: only the bare command asks for the link.
    [Fact]
    public async Task WebWithWordsAfterIt_IsATurn()
    {
        await _harness.ReceiveAsync();
        _harness.Enqueue(Update(TelegramPollingHarness.TextMessage("/web what is new on hacker news")));

        await _harness.RunAsync();

        _harness.Sent.ShouldBeEmpty();
        (await _harness.ReceiveAsync()).ShouldHaveSingleItem();
    }

    [Fact]
    public async Task AnOwnersCodeSentAsBindInAGroup_BindsItAndAdmitsItsLinkedMembers()
    {
        var code = _harness.Accounts.GroupCode();
        await _harness.ReceiveAsync();
        _harness.Enqueue(
            Update(TelegramPollingHarness.GroupMessage("/hello", username: "bob", userId: TelegramPollingHarness.BobId)),
            Update(TelegramPollingHarness.GroupMessage($"/bind@ziggurat_bot {code}")),
            Update(TelegramPollingHarness.GroupMessage("/hello", username: "bob", userId: TelegramPollingHarness.BobId)));

        await _harness.RunAsync();

        _harness.Sent.Count.ShouldBe(2);
        _harness.Sent[0].Text.ShouldContain("isn't bound to a household");
        _harness.Sent[1].Text.ShouldContain("household's now");
        var turn = (await _harness.ReceiveAsync()).ShouldHaveSingleItem().Message!;
        turn.Principal.ShouldBe(new Principal("telegram", "bob", FakeTelegramAccounts.AccountOf("bob").Account));
        turn.ConversationId.ShouldStartWith($"{TelegramPollingHarness.FamilyGroup}:");
    }

    [Fact]
    public async Task AnUnlinkedSenderInABoundGroup_IsRefused()
    {
        _harness.Accounts.Bind(TelegramPollingHarness.FamilyGroup);
        await _harness.ReceiveAsync();
        _harness.Enqueue(Update(TelegramPollingHarness.GroupMessage("/hello", username: "carol", userId: Carol)));

        await _harness.RunAsync();

        _harness.Sent.ShouldHaveSingleItem().Text.ShouldContain("ask an owner of the household for an invite");
        (await _harness.ReceiveAsync()).ShouldBeEmpty();
    }

    [Fact]
    public async Task ABoundGroup_AdmitsNobodyFromAnotherHousehold()
    {
        _harness.Accounts.Bind(TelegramPollingHarness.FamilyGroup);
        _harness.Accounts.Link(Carol, "carol", household: "household-elsewhere");
        await _harness.ReceiveAsync();
        _harness.Enqueue(Update(TelegramPollingHarness.GroupMessage("/hello", username: "carol", userId: Carol)));

        await _harness.RunAsync();

        _harness.Sent.ShouldHaveSingleItem();
        (await _harness.ReceiveAsync()).ShouldBeEmpty();
    }

    [Fact]
    public async Task BindInAPrivateChat_BindsNothing()
    {
        var code = _harness.Accounts.GroupCode();
        _harness.Enqueue(Update(TelegramPollingHarness.TextMessage($"/bind {code}")));

        await _harness.RunAsync();

        _harness.Sent.ShouldHaveSingleItem().Text.ShouldContain("in the group");
        (await _harness.Accounts.BindGroupAsync(TelegramPollingHarness.FamilyGroup, code, CancellationToken.None))
            .Outcome.ShouldBe(TelegramBindOutcome.Bound, "the code is still open");
    }

    // An outage is not a stranger: nobody is told to ask for an invite, nothing is emitted, and the
    // next message after the service is back is answered as it should have been.
    [Fact]
    public async Task AnAccountsOutage_IsSaidAsSuchAndRemembersNothing()
    {
        _harness.Accounts.Unreachable = true;
        await _harness.ReceiveAsync();
        _harness.EnqueueSequence(
            (TimeSpan.Zero, [Update(TelegramPollingHarness.TextMessage("/hello"))]),
            (TimeSpan.Zero, [Update(TelegramPollingHarness.TextMessage("/hello again"))]));
        _harness.OnBatch(1, () => _harness.Accounts.Unreachable = false);

        await _harness.RunAsync();

        _harness.Sent.ShouldHaveSingleItem().Text.ShouldContain("can't check who you are right now");
        (await _harness.ReceiveAsync()).ShouldHaveSingleItem().Message!.Content.ShouldBe("/hello again");
    }

    // A person's link rarely changes, so the bot asks about them once in a while rather than on every
    // message.
    [Fact]
    public async Task ALinkedSender_IsAskedAboutOnceWhileTheAnswerIsFresh()
    {
        await _harness.ReceiveAsync();
        _harness.EnqueueSequence(
            (TimeSpan.Zero, [Update(TelegramPollingHarness.TextMessage("/one"))]),
            (TimeSpan.FromMinutes(1), [Update(TelegramPollingHarness.TextMessage("/two"))]),
            (TimeSpan.FromMinutes(10), [Update(TelegramPollingHarness.TextMessage("/three"))]));

        await _harness.RunAsync();

        (await _harness.ReceiveAsync()).Count.ShouldBe(3);
        _harness.Accounts.Resolutions.ShouldBe(2);
    }

    // A refusal is kept for seconds, so somebody linked a moment ago (on another of the deployment's
    // bots, say) gets in at once.
    [Fact]
    public async Task ARefusal_IsForgottenWithinSeconds()
    {
        await _harness.ReceiveAsync();
        _harness.EnqueueSequence(
            (TimeSpan.Zero, [Update(TelegramPollingHarness.TextMessage("/hello", username: "carol", userId: Carol))]),
            (TimeSpan.FromSeconds(10), [Update(TelegramPollingHarness.TextMessage("/hello again", username: "carol", userId: Carol))]));
        _harness.OnBatch(1, () => _harness.Accounts.Link(Carol, "carol"));

        await _harness.RunAsync();

        _harness.Sent.ShouldHaveSingleItem();
        (await _harness.ReceiveAsync()).ShouldHaveSingleItem().Message!.Content.ShouldBe("/hello again");
    }

    private static Update Update(Message message) => new() { Id = 1, Message = message };

    public void Dispose() => _harness.Dispose();
}