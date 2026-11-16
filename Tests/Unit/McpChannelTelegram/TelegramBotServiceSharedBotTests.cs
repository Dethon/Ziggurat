using Domain.DTOs.Channel;
using Domain.DTOs.Household;
using McpChannelTelegram.McpTools;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Telegram.Bot.Types;

namespace Tests.Unit.McpChannelTelegram;

// One bot for every household. A linked person's message is their own household's turn and names no
// agent, so the household's default answers; /agent lists the household's agents and switches the
// chat to one, kept per chat; anybody not linked is shown only the ways in.
public class TelegramBotServiceSharedBotTests : IDisposable
{
    private const string Casa = "household-casa";
    private const long Carol = 3;

    private static readonly AgentsSection _homeAgents = new()
    {
        DefaultAgent = "chef",
        Agents =
        [
            new HouseholdAgent { Id = "chef", Template = "jonas", Name = "Chef" },
            new HouseholdAgent { Id = "sweet", Template = "sugary", Name = "Dulce" }
        ]
    };

    private readonly TelegramPollingHarness _harness = TelegramPollingHarness.OnTheSharedBot();

    public TelegramBotServiceSharedBotTests() => _harness.Households.Agents[FakeTelegramAccounts.Household] = _homeAgents;

    [Fact]
    public async Task ALinkedPersonsMessage_NamesNoAgent_SoTheirHouseholdsDefaultAnswers()
    {
        await _harness.ReceiveAsync();
        _harness.Enqueue(Update(TelegramPollingHarness.TextMessage("/what is for dinner")));

        await _harness.RunAsync();

        _harness.Sent.ShouldBeEmpty();
        var turn = (await _harness.ReceiveAsync()).ShouldHaveSingleItem().Message!;
        turn.AgentId.ShouldBeNull();
        turn.Principal.ShouldBe(new Principal("telegram", "alice", FakeTelegramAccounts.AccountOf("alice").Account));
    }

    [Fact]
    public async Task AgentAlone_ListsTheHouseholdsAgents_AndWhoAnswersHere()
    {
        await _harness.ReceiveAsync();
        _harness.Enqueue(Update(TelegramPollingHarness.TextMessage("/agent")));

        await _harness.RunAsync();

        var listed = _harness.Sent.ShouldHaveSingleItem().Text;
        listed.ShouldContain("Chef — /agent chef (answering here)");
        listed.ShouldContain("Dulce — /agent sweet");
        listed.ShouldNotContain("Dulce — /agent sweet (answering here)");
        (await _harness.ReceiveAsync()).ShouldBeEmpty();
    }

    [Fact]
    public async Task AHouseholdThatSavedNoAgents_ListsTheTwoTemplates()
    {
        _harness.Households.Agents.Clear();
        _harness.Enqueue(Update(TelegramPollingHarness.TextMessage("/agent")));

        await _harness.RunAsync();

        var listed = _harness.Sent.ShouldHaveSingleItem().Text;
        listed.ShouldContain("/agent jonas (answering here)");
        listed.ShouldContain("/agent sugary");
    }

    [Fact]
    public async Task AgentWithAnId_SwitchesTheChat_AndItsNextMessagesNameThatAgent()
    {
        await _harness.ReceiveAsync();
        _harness.Enqueue(
            Update(TelegramPollingHarness.TextMessage("/agent sweet")),
            Update(TelegramPollingHarness.TextMessage("/how many carbs in a banana")));

        await _harness.RunAsync();

        _harness.Sent.ShouldHaveSingleItem().Text.ShouldBe("Dulce answers here now.");
        (await _harness.ReceiveAsync()).ShouldHaveSingleItem().Message!.AgentId.ShouldBe("sweet");
    }

    [Fact]
    public async Task AChatsPick_OutlivesARestart()
    {
        _harness.Enqueue(Update(TelegramPollingHarness.TextMessage("/agent sweet")));
        await _harness.RunAsync();
        _harness.Restart();
        await _harness.ReceiveAsync();
        _harness.Enqueue(Update(TelegramPollingHarness.TextMessage("/how many carbs in a banana")));

        await _harness.RunAsync();

        (await _harness.ReceiveAsync()).ShouldHaveSingleItem().Message!.AgentId.ShouldBe("sweet");
    }

    // The pick is the chat's: Bob's own chat with the bot still has the household's default.
    [Fact]
    public async Task APick_IsTheChatsAlone()
    {
        await _harness.ReceiveAsync();
        _harness.Enqueue(
            Update(TelegramPollingHarness.TextMessage("/agent sweet", chatId: TelegramPollingHarness.AliceId)),
            Update(TelegramPollingHarness.TextMessage(
                "/hello", chatId: TelegramPollingHarness.BobId, username: "bob", userId: TelegramPollingHarness.BobId)));

        await _harness.RunAsync();

        (await _harness.ReceiveAsync()).ShouldHaveSingleItem().Message!.AgentId.ShouldBeNull();
    }

    [Fact]
    public async Task AnAgentTheHouseholdDoesNotHave_IsRefused_AndChangesNothing()
    {
        await _harness.ReceiveAsync();
        _harness.Enqueue(
            Update(TelegramPollingHarness.TextMessage("/agent jack")),
            Update(TelegramPollingHarness.TextMessage("/hello")));

        await _harness.RunAsync();

        var refused = _harness.Sent.ShouldHaveSingleItem().Text;
        refused.ShouldContain("no agent called 'jack'");
        refused.ShouldContain("/agent chef (answering here)");
        (await _harness.ReceiveAsync()).ShouldHaveSingleItem().Message!.AgentId.ShouldBeNull();
    }

    // Each person is answered with their own household's agents, read on their own assertion.
    [Fact]
    public async Task APersonOfAnotherHousehold_IsShownTheirOwnHouseholdsAgents()
    {
        _harness.Accounts.Link(Carol, "carol", Casa);
        _harness.Households.Agents[Casa] = new AgentsSection
        {
            Agents = [new HouseholdAgent { Id = "abuela", Template = "jonas", Name = "Abuela" }]
        };
        _harness.Enqueue(Update(TelegramPollingHarness.TextMessage("/agent", chatId: Carol, username: "carol", userId: Carol)));

        await _harness.RunAsync();

        var listed = _harness.Sent.ShouldHaveSingleItem().Text;
        listed.ShouldContain("Abuela — /agent abuela");
        listed.ShouldNotContain("Chef");
    }

    [Fact]
    public async Task AStranger_IsShownOnlyTheWaysIn()
    {
        var invite = _harness.Accounts.Invite("Carol", Casa);
        await _harness.ReceiveAsync();
        _harness.Enqueue(
            Update(TelegramPollingHarness.TextMessage("/agent", chatId: Carol, username: "carol", userId: Carol)),
            Update(TelegramPollingHarness.TextMessage("/what is for dinner", chatId: Carol, username: "carol", userId: Carol)),
            Update(TelegramPollingHarness.TextMessage($"/start {invite}", chatId: Carol, username: "carol", userId: Carol)));

        await _harness.RunAsync();

        _harness.Sent.Select(s => s.Text).ShouldSatisfyAllConditions(
            sent => sent.Count().ShouldBe(3),
            sent => sent.Take(2).ShouldAllBe(text => text.Contains("ask an owner of the household for an invite")),
            sent => sent.Last().ShouldContain("Welcome, Carol"));
        (await _harness.ReceiveAsync()).ShouldBeEmpty();
    }

    // A group is the household's whose owner's code bound it: its members' turns are theirs, the pick is
    // the group's, and a person of another household is not answered there.
    [Fact]
    public async Task AGroupBoundWithAnOwnersCode_IsThatHouseholds()
    {
        _harness.Accounts.Link(Carol, "carol", Casa);
        _harness.Households.Agents[Casa] = new AgentsSection
        {
            Agents = [new HouseholdAgent { Id = "abuela", Template = "jonas", Name = "Abuela" }]
        };
        var code = _harness.Accounts.GroupCode(Casa);
        await _harness.ReceiveAsync();
        _harness.Enqueue(
            Update(TelegramPollingHarness.GroupMessage($"/bind {code}", username: "carol", userId: Carol)),
            Update(TelegramPollingHarness.GroupMessage("/agent abuela", username: "carol", userId: Carol)),
            Update(TelegramPollingHarness.GroupMessage("/dinner ideas", username: "carol", userId: Carol)),
            Update(TelegramPollingHarness.GroupMessage("/dinner ideas")));

        await _harness.RunAsync();

        _harness.Sent.Select(s => s.Text).ShouldSatisfyAllConditions(
            sent => sent.ElementAt(1).ShouldBe("Abuela answers here now."),
            sent => sent.Last().ShouldContain("another household"));
        var turn = (await _harness.ReceiveAsync()).ShouldHaveSingleItem().Message!;
        turn.AgentId.ShouldBe("abuela");
        turn.Principal!.Account!.TenantId.ShouldBe(Casa);
    }

    [Fact]
    public async Task AnAccountsOutageWhileListing_SaysSo_AndSwitchesNothing()
    {
        await _harness.ReceiveAsync();
        _harness.OnBatch(0, () => _harness.Households.Unreachable = true);
        _harness.OnBatch(1, () => _harness.Households.Unreachable = false);
        _harness.EnqueueSequence(
            (TimeSpan.Zero, [Update(TelegramPollingHarness.TextMessage("/agent sweet"))]),
            (TimeSpan.Zero, [Update(TelegramPollingHarness.TextMessage("/hello"))]));

        await _harness.RunAsync();

        _harness.Sent.ShouldHaveSingleItem().Text.ShouldContain("can't read your household's agents");
        (await _harness.ReceiveAsync()).ShouldHaveSingleItem().Message!.AgentId.ShouldBeNull();
    }

    // A household with no bot of its own is rung on the shared bot.
    [Fact]
    public async Task AnAlert_RingsOnTheSharedBot_WhenTheHouseholdHasNoBotOfItsOwn()
    {
        var services = new ServiceCollection()
            .AddSingleton(_harness.BotRegistry)
            .AddSingleton(_harness.CallbackRouter)
            .AddSingleton(_harness.Access)
            .BuildServiceProvider();

        _ = OfferAlertTool.RunAsync(new OfferAlertParams
        {
            AlertId = Guid.NewGuid().ToString("N"),
            Owner = FakeTelegramAccounts.AccountOf("alice").Account,
            Text = "pasta is ready",
            AgentId = "chef"
        }, services, CancellationToken.None);

        await Eventually.Until(() => _harness.Sent.Count == 1, "the alert to be sent");
        _harness.Sent.ShouldHaveSingleItem().ChatId.Identifier.ShouldBe(TelegramPollingHarness.AliceId);
    }

    public void Dispose() => _harness.Dispose();

    private static Update Update(Message message) => new() { Id = 1, Message = message };
}