using StackExchange.Redis;

namespace HouseholdMigration.Steps;

// The chat client's phones were subscribed under the name its picker sent; the hub now pushes to the
// account's (.claude/rules/channels.md). Each phone moves with its keys and its space untouched: the
// space sets hold endpoints, not people, so only the person's hash and the endpoint's owner change.
// A phone whose endpoint somebody else has subscribed since is theirs, and its stale entry just goes.
public sealed class PushSubscriptions(IConnectionMultiplexer redis) : IMigrationStep
{
    // RedisPushSubscriptionStore's key shapes.
    private const string PersonPrefix = "push:subs:";
    private const string EndpointPrefix = "push:ep:";

    public async Task<StepReport> RunAsync(Household household, bool dryRun, CancellationToken ct)
    {
        var report = new StepReport("Push subscriptions");
        var db = redis.GetDatabase();
        foreach (var person in household.People)
        {
            var (moved, from) = (0, new List<string>());
            foreach (var old in person.Entry.Names.Where(n => n != person.UserId))
            {
                var phones = await db.HashGetAllAsync(PersonPrefix + old);
                if (phones.Length == 0)
                {
                    continue;
                }

                from.Add(old);
                foreach (var phone in phones)
                {
                    var owner = await db.StringGetAsync(EndpointPrefix + phone.Name);
                    var theirs = owner.IsNullOrEmpty || owner == old;
                    if (!dryRun)
                    {
                        if (theirs)
                        {
                            await db.HashSetAsync(PersonPrefix + person.UserId, phone.Name, phone.Value);
                            await db.StringSetAsync(EndpointPrefix + phone.Name, person.UserId);
                        }

                        await db.HashDeleteAsync(PersonPrefix + old, phone.Name);
                    }

                    moved += theirs ? 1 : 0;
                    report.Changes++;
                }
            }

            if (from.Count > 0)
            {
                report.Lines.Add(
                    $"{person.Label}: {moved} phone{(moved == 1 ? "" : "s")} from {string.Join(", ", from.Select(n => $"'{n}'"))}");
            }
        }

        return report;
    }
}