using Domain.Metrics;
using StackExchange.Redis;

namespace HouseholdMigration.Steps;

// The dashboard's roll-ups from before households were everyone's, and everyone was the home
// household, so each moves under the home's scope (MetricKeys): a day's time series, its totals and
// the recent errors. From the deploy on, the collector writes the new keys, so the day of the deploy
// is in both shapes by the time this runs: a roll-up is merged into what the home counted since,
// never renamed over it — a time series by union, a totals hash field by field, the errors behind
// the newer ones. The merged key keeps the home's expiry, or the old key's where the home's had
// none, so the move does not make thirty days' history immortal.
//
// Each key is merged and deleted in one transaction, so a run that stopped part way is finished by
// running it again without counting a day twice.
public sealed class MetricRollups(IConnectionMultiplexer redis) : IMigrationStep
{
    private const int RecentErrors = 100;

    public async Task<StepReport> RunAsync(Household household, bool dryRun, CancellationToken ct)
    {
        var report = new StepReport("Metric roll-ups");
        var db = redis.GetDatabase();
        var server = redis.GetServer(redis.GetEndPoints()[0]);
        var moves = new List<(string From, string To, string Family)>();
        await foreach (var key in server.KeysAsync(db.Database, "metrics:*").WithCancellation(ct))
        {
            var name = key.ToString();
            if (MetricKeys.Legacy(name) is var (family, date))
            {
                moves.Add((name, MetricKeys.Daily(family, household.TenantId, date), family));
            }
            else if (name == MetricKeys.LegacyRecentErrors)
            {
                moves.Add((name, MetricKeys.RecentErrors(household.TenantId), "recent errors"));
            }
        }

        if (!dryRun && moves.Count > 0)
        {
            foreach (var (from, to, _) in moves)
            {
                await MergeAsync(db, from, to);
            }

            await db.SetAddAsync(MetricKeys.Scopes, household.TenantId);
        }

        report.Changes = moves.Count;
        report.Lines.AddRange(moves
            .GroupBy(m => m.Family)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => g.Key == "recent errors"
                ? $"the recent errors: into the home household ({household.TenantId})"
                : $"{g.Key}: {Counted.Of(g.Count(), "day")} into the home household ({household.TenantId})"));
        return report;
    }

    private static async Task MergeAsync(IDatabase db, string from, string to)
    {
        // Read before the merge and set again after it: storing a union replaces the home's key, and
        // its expiry with it.
        var expiry = await db.KeyTimeToLiveAsync(to) ?? await db.KeyTimeToLiveAsync(from);
        var merge = db.CreateTransaction();
        merge.AddCondition(Condition.KeyExists(from));
        switch (await db.KeyTypeAsync(from))
        {
            case RedisType.SortedSet:
                _ = merge.SortedSetCombineAndStoreAsync(SetOperation.Union, to, [to, from], aggregate: Aggregate.Max);
                break;
            case RedisType.Hash:
                foreach (var entry in await db.HashGetAllAsync(from))
                {
                    Task _ = long.TryParse(entry.Value.ToString(), out var whole)
                        ? merge.HashIncrementAsync(to, entry.Name, whole)
                        : merge.HashIncrementAsync(to, entry.Name, (double)entry.Value);
                }

                break;
            case RedisType.List:
                // Newest first, as the collector pushes them: the older list goes behind.
                _ = merge.ListRightPushAsync(to, await db.ListRangeAsync(from));
                _ = merge.ListTrimAsync(to, 0, RecentErrors - 1);
                break;
        }

        if (expiry is { } left)
        {
            _ = merge.KeyExpireAsync(to, left);
        }

        _ = merge.KeyDeleteAsync(from);
        await merge.ExecuteAsync();
    }
}