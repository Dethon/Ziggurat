using Domain.DTOs.Household;
using McpChannelTelegram.Services;
using Moq;
using Shouldly;
using Telegram.Bot;

namespace Tests.Unit.McpChannelTelegram;

public class BotRegistryTests
{
    private const string Home = "household-home";
    private const string Casa = "household-casa";

    private readonly Dictionary<string, ITelegramBotClient> _connected = [];

    private BotRegistry Registry(string? sharedToken = "1000:shared") =>
        new(sharedToken, token => _connected[token] = new Mock<ITelegramBotClient>().Object);

    [Fact]
    public void Lookup_WithUnknownIds_ReturnsNull()
    {
        var registry = Registry();
        registry.Replace([new HouseholdTelegramBot(Home, "jack", "1001:jack")]);

        registry.GetBotForChat(999).ShouldBeNull();
        registry.ForReference("household-home.nabu").ShouldBeNull();
        registry.Of(Casa, "jack").ShouldBeNull();
    }

    [Fact]
    public void RegisterChat_OverwritesPreviousMapping()
    {
        var registry = Registry();
        registry.Replace([new HouseholdTelegramBot(Home, "jack", "1001:jack"), new HouseholdTelegramBot(Home, "jonas", "1002:jonas")]);

        registry.RegisterChat(100, registry.Of(Home, "jack")!);
        registry.RegisterChat(100, registry.Of(Home, "jonas")!);

        registry.GetBotForChat(100).ShouldBe(registry.Of(Home, "jonas")!.Client);
    }

    // A save that leaves a bot as it was leaves the very bot polling; a changed token is a new bot, and
    // a bot no longer configured is gone, with the chats it answered.
    [Fact]
    public void Replace_StartsWhatIsNewAndStopsWhatIsGone_AndKeepsWhatIsUnchanged()
    {
        var registry = Registry();
        registry.Replace([new HouseholdTelegramBot(Home, "jack", "1001:jack"), new HouseholdTelegramBot(Home, "jonas", "1002:jonas")]);
        var jack = registry.Of(Home, "jack")!;
        var jonas = registry.Of(Home, "jonas")!;
        registry.RegisterChat(100, jonas);

        var changes = registry.Replace(
            [new HouseholdTelegramBot(Home, "jack", "1001:jack"), new HouseholdTelegramBot(Casa, "jonas", "2002:jonas")]);

        changes.Started.Select(b => b.Key).ShouldBe(["household-casa.jonas"]);
        changes.Stopped.ShouldBe([jonas]);
        registry.Of(Home, "jack").ShouldBeSameAs(jack);
        registry.GetBotForChat(100).ShouldBeNull();
        registry.All.Select(b => b.Key).ShouldBe(["shared", "household-home.jack", "household-casa.jonas"]);
    }

    // Telegram answers one poller per token: a token configured twice, or the shared bot's, is polled once.
    [Fact]
    public void Replace_PollsEachTokenOnce()
    {
        var registry = Registry();

        registry.Replace(
        [
            new HouseholdTelegramBot(Home, "jack", "1001:jack"),
            new HouseholdTelegramBot(Casa, "jack", "1001:jack"),
            new HouseholdTelegramBot(Casa, "jonas", "1000:shared")
        ]);

        registry.All.Select(b => b.Key).ShouldBe(["shared", "household-home.jack"]);
    }

    // Who writes first to a person: their household's bot for the agent, its other bots, the shared bot
    // last — and never another household's bot.
    [Fact]
    public void BotsPreferring_TheHouseholdsBotForTheAgent_ThenItsOthers_ThenTheShared()
    {
        var registry = Registry();
        registry.Replace(
        [
            new HouseholdTelegramBot(Home, "jack", "1001:jack"),
            new HouseholdTelegramBot(Home, "jonas", "1002:jonas"),
            new HouseholdTelegramBot(Casa, "jonas", "2002:jonas")
        ]);

        registry.BotsPreferring(Home, "jonas")
            .ShouldBe([registry.Of(Home, "jonas")!.Client, registry.Of(Home, "jack")!.Client, registry.Shared!.Client]);
        registry.BotsPreferring("household-new", "jonas").ShouldBe([registry.Shared!.Client]);
    }

    [Fact]
    public void AStackWithNoSharedToken_PollsTheHouseholdsBotsAlone()
    {
        var registry = Registry(sharedToken: "");
        registry.Replace([new HouseholdTelegramBot(Home, "jack", "1001:jack")]);

        registry.Shared.ShouldBeNull();
        registry.All.Select(b => b.Key).ShouldBe(["household-home.jack"]);
        registry.ForReference(TelegramBot.SharedKey).ShouldBeNull();
    }
}