using Tests.Integration.Fixtures;

namespace Tests.Integration.Migration;

// A Redis Stack of the migration's own. The memory index lives on database 0 alone, so the search
// pools share it between classes — and the migration moves the 'household' bucket, scans every topic
// and lists every schedule, which would take a sibling class's rows from under it mid-run. A pool by a
// name nobody else asks for is a container nobody else writes to.
public sealed class HouseholdMigrationRedisFixture : RedisFixture
{
    protected override async Task<RedisLease> AcquireAsync() =>
        (await RedisPool.GetAsync("household-migration")).ShareSearchDatabase();
}