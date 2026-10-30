using System.Text.RegularExpressions;
using Domain.Contracts;
using Domain.DTOs;

namespace HouseholdMigration.Steps;

// Memory was filed under whatever sender a channel reported, so one person is several buckets: their
// Telegram username, their chat-client name, their voice profile, a first name. Each is merged into
// the one bucket recall now reads, the account's (.claude/rules/memory-architecture.md), through the
// store's own writes so the index files them under the account. Ids, embeddings and timestamps travel
// as they are; a memory whose text the account already holds is the same fact said twice, and is
// dropped rather than kept as a second copy.
//
// The buckets that were nobody's — 'household' (the voice guest), 'scheduler' and 'watch' (fires),
// 'system' (download alerts) — go to the owner, the one person who can see and forget them.
public sealed partial class MemoryBuckets(IMemoryStore store) : IMigrationStep
{
    public static readonly IReadOnlyList<string> Owners = ["household", "scheduler", "watch", "system"];

    public async Task<StepReport> RunAsync(Household household, bool dryRun, CancellationToken ct)
    {
        var report = new StepReport("Memories");
        foreach (var person in household.People)
        {
            await MergeAsync(person, person.Entry.Names, withProfile: true, dryRun, report, ct);
        }

        await MergeAsync(household.Owner, Owners, withProfile: false, dryRun, report, ct);

        // A bucket under a name nobody was listed with is somebody's, and only the operator knows whose.
        var claimed = household.People.SelectMany(p => p.Entry.Names).Concat(Owners)
            .ToHashSet(StringComparer.Ordinal);
        var unclaimed = (await store.GetAllUserIdsAsync(ct))
            .Where(id => !AccountId().IsMatch(id) && !claimed.Contains(id))
            .Order(StringComparer.Ordinal)
            .ToList();
        if (unclaimed.Count > 0)
        {
            report.Lines.Add(
                $"left under names nobody is listed with: {string.Join(", ", unclaimed.Select(n => $"'{n}'"))} "
                + "— add each to its person's oldNames and run again");
        }

        return report;
    }

    private async Task MergeAsync(
        MigratedPerson into, IReadOnlyList<string> names, bool withProfile, bool dryRun, StepReport report,
        CancellationToken ct)
    {
        var sources = names.Where(n => n != into.UserId).ToList();
        IReadOnlyList<MemoryEntry> held = into.UserId is null ? [] : await store.GetByUserIdAsync(into.UserId, ct);
        var known = held.Select(m => Fact(m.Content)).ToHashSet(StringComparer.Ordinal);
        var (moved, dropped, from) = (0, 0, new List<string>());

        // One write after another: each memory is checked against everything merged before it.
        foreach (var source in sources)
        {
            var memories = (await store.GetByUserIdAsync(source, ct)).OrderBy(m => m.CreatedAt).ToList();
            if (memories.Count == 0)
            {
                continue;
            }

            from.Add(source);
            foreach (var memory in memories)
            {
                var isNew = known.Add(Fact(memory.Content));
                if (!dryRun)
                {
                    if (isNew)
                    {
                        await store.StoreAsync(memory with { UserId = into.UserId! }, ct);
                    }

                    await store.DeleteAsync(source, memory.Id, ct);
                }

                (moved, dropped) = isNew ? (moved + 1, dropped) : (moved, dropped + 1);
            }
        }

        var profile = withProfile ? await ProfileAsync(into, sources, dryRun, ct) : null;
        report.Changes += moved + dropped + (profile is null ? 0 : 1);
        if (moved + dropped > 0 || profile is not null)
        {
            report.Lines.Add(
                $"{into.Label}: {moved} from {string.Join(", ", from.Select(n => $"'{n}'"))}"
                + (dropped > 0 ? $", {dropped} already known and dropped" : "")
                + (profile is null ? "" : $"; {profile}"));
        }
    }

    // The profile recall reads beside the memories: the latest of the old names', unless the account
    // has one of its own already. The rest describe the same person less recently, and go.
    private async Task<string?> ProfileAsync(
        MigratedPerson into, IReadOnlyList<string> sources, bool dryRun, CancellationToken ct)
    {
        var profiles = new List<PersonalityProfile>();
        foreach (var source in sources)
        {
            if (await store.GetProfileAsync(source, ct) is { } profile)
            {
                profiles.Add(profile);
            }
        }

        if (profiles.Count == 0)
        {
            return null;
        }

        var latest = profiles.MaxBy(p => p.LastUpdated)!;
        var kept = into.UserId is not null && await store.GetProfileAsync(into.UserId, ct) is not null;
        if (!dryRun)
        {
            if (!kept)
            {
                await store.SaveProfileAsync(latest with { UserId = into.UserId! }, ct);
            }

            foreach (var profile in profiles)
            {
                await store.DeleteProfileAsync(profile.UserId, ct);
            }
        }

        return kept ? "their own profile kept" : $"the profile from '{latest.UserId}'";
    }

    private static string Fact(string content) => content.Trim();

    // The accounts service's ids: what every bucket is keyed by from now on.
    [GeneratedRegex("^[0-9a-f]{32}$")]
    private static partial Regex AccountId();
}