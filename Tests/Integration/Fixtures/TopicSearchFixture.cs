namespace Tests.Integration.Fixtures;

// The Redis a topic-search test needs. RediSearch indexes database 0 and refuses every other one,
// so a class that searches topics cannot be separated by database the way RedisFixture separates
// the rest. Its own pool, because the topic index and the memory index are built by different
// stores over different prefixes and nothing is served by sharing a container between them.
public sealed class TopicSearchFixture : RedisFixture
{
    protected override async Task<RedisLease> AcquireAsync()
    {
        var pool = await RedisPool.GetAsync("search-topics");
        return pool.ShareSearchDatabase();
    }
}