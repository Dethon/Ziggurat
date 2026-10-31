using Infrastructure.StateManagers;
using StackExchange.Redis;

namespace HouseholdMigration.Steps;

// A space is its household's now, and so are the phones that hear of it
// (`push:space@{tenantId}:{slug}`, RedisPushSubscriptionStore). Every space's phones still under the
// shape from before households — `push:space:{slug}` — were the home household's, and join its space
// of the same name. The new shape's '@' keeps a household's space out of this step's pattern, so a
// rerun after a second household exists moves nothing of theirs. Phones are endpoints, not people:
// the person each one is is untouched (PushSubscriptions moved that).
public sealed class PushSpaces(IConnectionMultiplexer redis) : IMigrationStep
{
    // The old shape, spelled here only to read and clear what is still under it.
    private const string UnmovedPrefix = "push:space:";

    public async Task<StepReport> RunAsync(Household household, bool dryRun, CancellationToken ct)
    {
        var report = new StepReport("Push spaces into the household");
        var db = redis.GetDatabase();
        var server = redis.GetServer(redis.GetEndPoints()[0]);
        var spaces = new List<string>();
        await foreach (var key in server.KeysAsync(db.Database, $"{UnmovedPrefix}*").WithCancellation(ct))
        {
            spaces.Add(key.ToString());
        }

        foreach (var space in spaces.Order(StringComparer.Ordinal))
        {
            var slug = space[UnmovedPrefix.Length..];
            var phones = await db.SetMembersAsync(space);
            report.Changes++;
            report.Lines.Add($"space '{slug}': {Counted.Of(phones.Length, "phone")}");
            if (dryRun)
            {
                continue;
            }

            var move = db.CreateTransaction();
            if (phones.Length > 0)
            {
                _ = move.SetAddAsync(RedisPushSubscriptionStore.SpaceKey(household.TenantId, slug), phones);
            }

            _ = move.KeyDeleteAsync(space);
            await move.ExecuteAsync();
        }

        return report;
    }
}