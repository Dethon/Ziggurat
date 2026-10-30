using System.Text.Json;
using Domain.Contracts;
using Domain.DTOs.WebChat;
using StackExchange.Redis;

namespace HouseholdMigration.Steps;

// The chat client's phones were subscribed under the name its picker sent; the hub now pushes to the
// account's (.claude/rules/channels.md). Each phone is saved again under the account by the store's own
// save, with its keys and its space as they were: the space sets hold endpoints, not people, so only
// the person's hash and the endpoint's owner change. A phone whose endpoint somebody else has
// subscribed since is theirs, and its stale entry just goes.
public sealed class PushSubscriptions(IConnectionMultiplexer redis, IPushSubscriptionStore store) : IMigrationStep
{
    // RedisPushSubscriptionStore's key shapes, spelled here only to read and clear what is still under
    // an old name.
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
                            var (subscription, space) = Read(phone);
                            await store.SaveAsync(person.UserId!, subscription, space, ct: ct);
                        }

                        // The save clears the old name's entry only where the endpoint still named it as
                        // its owner; one nobody indexed, or somebody else's now, is cleared here.
                        await db.HashDeleteAsync(PersonPrefix + old, phone.Name);
                    }

                    moved += theirs ? 1 : 0;
                    report.Changes++;
                }
            }

            if (from.Count > 0)
            {
                report.Lines.Add(
                    $"{person.Label}: {Counted.Of(moved, "phone")} from {string.Join(", ", from.Select(n => $"'{n}'"))}");
            }
        }

        return report;
    }

    // The store's entry: the endpoint is the hash field, the keys and the space its JSON value, in the
    // store's default (PascalCase) spelling. One saved before spaces existed was in the default one.
    private static (PushSubscriptionDto Subscription, string Space) Read(HashEntry phone)
    {
        var value = JsonSerializer.Deserialize<StoredPhone>(phone.Value.ToString())!;
        return (new PushSubscriptionDto(phone.Name!, value.P256dh, value.Auth), value.SpaceSlug ?? "default");
    }

    private sealed record StoredPhone(string P256dh, string Auth, string? SpaceSlug);
}