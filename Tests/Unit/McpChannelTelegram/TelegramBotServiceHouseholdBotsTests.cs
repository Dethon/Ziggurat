using Domain.DTOs.Household;
using Moq;
using Shouldly;
using Telegram.Bot;
using Telegram.Bot.Requests;
using Telegram.Bot.Types;

namespace Tests.Unit.McpChannelTelegram;

// A household's own bots are its configuration's (its keys section's telegramBots), each bound to one
// of its agents: polled from the moment a save names it, stopped the moment one drops it, and answering
// the household's own people alone, always with its agent.
public class TelegramBotServiceHouseholdBotsTests
{
    private const string Casa = "household-casa";
    private const string ChefToken = "2001:casa-chef";
    private const long Carol = 3;

    [Fact]
    public async Task AHouseholdsBot_AnswersWithItsAgent()
    {
        using var harness = new TelegramPollingHarness();
        await harness.ReceiveAsync();
        harness.Enqueue(Update(TelegramPollingHarness.TextMessage("/hello")));

        await harness.RunAsync();

        (await harness.ReceiveAsync()).ShouldHaveSingleItem().Message!.AgentId.ShouldBe(TelegramPollingHarness.AgentId);
    }

    // The home's jack bot is the home's: somebody of another household is told so and gets no turn.
    [Fact]
    public async Task AHouseholdsBot_AnswersNobodyOfAnotherHousehold()
    {
        using var harness = new TelegramPollingHarness();
        harness.Accounts.Link(Carol, "carol", Casa);
        await harness.ReceiveAsync();
        harness.Enqueue(Update(TelegramPollingHarness.TextMessage("/hello", chatId: Carol, username: "carol", userId: Carol)));

        await harness.RunAsync();

        harness.Sent.ShouldHaveSingleItem().Text.ShouldContain("another household");
        (await harness.ReceiveAsync()).ShouldBeEmpty();
    }

    [Fact]
    public async Task AgentOnAHouseholdsBot_SaysTheBotIsItsAgents_AndSwitchesNothing()
    {
        using var harness = new TelegramPollingHarness();
        await harness.ReceiveAsync();
        harness.Enqueue(
            Update(TelegramPollingHarness.TextMessage("/agent sugary")),
            Update(TelegramPollingHarness.TextMessage("/hello")));

        await harness.RunAsync();

        harness.Sent.ShouldHaveSingleItem().Text.ShouldContain("This bot is jack's");
        (await harness.ReceiveAsync()).ShouldHaveSingleItem().Message!.AgentId.ShouldBe(TelegramPollingHarness.AgentId);
    }

    // Saved while the server runs, a household's bot starts polling with no restart and answers with the
    // agent it is bound to; saved away again, it stops.
    [Fact]
    public async Task ABotSavedInAHouseholdsKeys_StartsPolling_AndStopsOnceItIsRemoved()
    {
        using var harness = TelegramPollingHarness.OnTheSharedBot();
        harness.Accounts.Link(Carol, "carol", Casa);
        GivenTheSharedBotIsQuiet(harness);
        var chef = new Mock<ITelegramBotClient>();
        var polls = 0;
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        chef.Setup(b => b.SendRequest(It.IsAny<GetUpdatesRequest>(), It.IsAny<CancellationToken>()))
            .Returns(async Task<Update[]> (GetUpdatesRequest _, CancellationToken ct) =>
            {
                if (Interlocked.Increment(ref polls) == 1)
                {
                    return [Update(TelegramPollingHarness.TextMessage("/hello", chatId: Carol, username: "carol", userId: Carol))];
                }

                try
                {
                    await Task.Delay(Timeout.Infinite, ct);
                }
                finally
                {
                    stopped.TrySetResult();
                }

                return [];
            });
        harness.OtherBots[ChefToken] = chef;
        await harness.ReceiveAsync();
        await harness.Service.StartAsync(CancellationToken.None);

        harness.Households.SaveBots(new HouseholdTelegramBot(Casa, "chef", ChefToken));
        await DriveUntilAsync(harness, () => Volatile.Read(ref polls) >= 2, "the saved bot to be polled");
        var turn = (await harness.ReceiveAsync()).ShouldHaveSingleItem().Message!;
        harness.Households.SaveBots();
        await DriveUntilAsync(harness, () => stopped.Task.IsCompleted, "the removed bot to stop polling");
        await harness.Service.StopAsync(CancellationToken.None);

        turn.AgentId.ShouldBe("chef");
        turn.Principal!.Account!.TenantId.ShouldBe(Casa);
        harness.BotRegistry.All.ShouldBe([harness.Bot]);
    }

    // An accounts service that cannot answer is no household with no bots: what was polling goes on.
    [Fact]
    public async Task AnAccountsOutage_LeavesTheBotsPollingAsTheyWere()
    {
        using var harness = new TelegramPollingHarness();
        harness.Households.Unreachable = true;
        await harness.ReceiveAsync();
        harness.Enqueue(Update(TelegramPollingHarness.TextMessage("/hello")));

        await harness.RunAsync();

        (await harness.ReceiveAsync()).ShouldHaveSingleItem().Message!.AgentId.ShouldBe(TelegramPollingHarness.AgentId);
    }

    // The refresh is a PeriodicTimer, armed once: the clock is driven an interval at a time until the
    // effect appears.
    private static async Task DriveUntilAsync(TelegramPollingHarness harness, Func<bool> done, string because) =>
        await Eventually.Until(() =>
        {
            if (!done())
            {
                harness.Time.Advance(harness.Bots.Refresh);
            }

            return done();
        }, because);

    private static void GivenTheSharedBotIsQuiet(TelegramPollingHarness harness) =>
        harness.BotClient.Setup(b => b.SendRequest(It.IsAny<GetUpdatesRequest>(), It.IsAny<CancellationToken>()))
            .Returns(async Task<Update[]> (GetUpdatesRequest _, CancellationToken ct) =>
            {
                await Task.Delay(Timeout.Infinite, ct);
                return [];
            });

    private static Update Update(Message message) => new() { Id = 1, Message = message };
}