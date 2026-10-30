using Domain.Contracts;
using Infrastructure.StateManagers;
using StackExchange.Redis;

namespace HouseholdMigration.Steps;

// A person's remembered web logins were sealed under the identity map's Name, with that name as the
// associated data, so the record opens only under it; the browse server now opens a record only under
// the account's user id (.claude/rules/web-browsing.md). Each is opened the old way and sealed the new
// one with the same key, then the old one goes. An account that already remembers logins of its own
// keeps them, and the old record is left for the operator: merging two browsers' cookies is a guess.
public sealed class RememberedLogins(IConnectionMultiplexer redis, IPersonStateStore logins) : IMigrationStep
{
    // RedisPersonStateStore's key shape, before and after: only the name in it changes.
    private const string KeyPrefix = "websearch:person:";

    public async Task<StepReport> RunAsync(Household household, bool dryRun, CancellationToken ct)
    {
        var report = new StepReport("Remembered web logins");
        var db = redis.GetDatabase();
        foreach (var person in household.People)
        {
            var old = person.Entry.Name;
            if (old == person.UserId || !await db.KeyExistsAsync(KeyPrefix + old))
            {
                continue;
            }

            if (person.UserId is not null && await db.KeyExistsAsync(KeyPrefix + person.UserId))
            {
                report.Problems.Add(
                    $"{person.Label} remembers logins of their own already, so the record under '{old}' was not "
                    + $"moved onto them; delete {KeyPrefix}{old} once it is not wanted");
                continue;
            }

            PersonState? state;
            try
            {
                state = await logins.LoadAsync(old, ct);
            }
            catch (PersonStateUnreadableException ex)
            {
                report.Problems.Add($"the record under '{old}' could not be opened: {ex.Message}");
                continue;
            }

            if (state is null)
            {
                // The first, never-deployed format, which the browse server reads as nothing remembered.
                continue;
            }

            if (!dryRun)
            {
                await logins.SaveAsync(person.UserId!, state, ct);
                await db.KeyDeleteAsync(KeyPrefix + old);
            }

            report.Changes++;
            report.Lines.Add(
                $"{person.Label}: {Counted.Of(state.Logins.Count, "login")} re-sealed from '{old}'");
        }

        return report;
    }
}