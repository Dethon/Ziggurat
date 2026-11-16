using McpChannelTelegram.Services;
using Shouldly;
using Tests.Integration.Fixtures;

namespace Tests.Integration.Channels;

// The agent a chat on the shared bot picked is kept in Redis, so the server that comes back after a
// restart reads it again, and a pick is only ever read for the household and chat it was made in.
public class RedisChatAgentChoicesTests(RedisFixture redis) : IClassFixture<RedisFixture>
{
    [Fact]
    public async Task APick_IsReadByAServerStartedAfterwards_ForItsOwnHouseholdAndChatAlone()
    {
        var household = $"household-{Guid.NewGuid():N}";
        await new RedisChatAgentChoices(redis.Connection).SetAsync(household, 42, "sweet", CancellationToken.None);

        var restarted = new RedisChatAgentChoices(redis.Connection);

        (await restarted.GetAsync(household, 42, CancellationToken.None)).ShouldBe("sweet");
        (await restarted.GetAsync(household, 43, CancellationToken.None)).ShouldBeNull();
        (await restarted.GetAsync($"household-{Guid.NewGuid():N}", 42, CancellationToken.None)).ShouldBeNull();
    }

    [Fact]
    public async Task APickMadeAgain_ReplacesTheOneBefore()
    {
        var household = $"household-{Guid.NewGuid():N}";
        var choices = new RedisChatAgentChoices(redis.Connection);

        await choices.SetAsync(household, -100_200, "sweet", CancellationToken.None);
        await choices.SetAsync(household, -100_200, "chef", CancellationToken.None);

        (await choices.GetAsync(household, -100_200, CancellationToken.None)).ShouldBe("chef");
    }
}