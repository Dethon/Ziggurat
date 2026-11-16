using Domain.Contracts;
using Domain.DTOs;
using Domain.DTOs.Household;
using Domain.DTOs.Metrics;
using Domain.Metrics;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Infrastructure.Memory;

public record MemoryDreamingOptions
{
    public string CronSchedule { get; init; } = "0 3 * * *";
    public int DecayDays { get; init; } = 30;
    public double DecayFactor { get; init; } = 0.9;
    public double DecayFloor { get; init; } = 0.1;
    public MemoryCategory[] DecayExemptCategories { get; init; } = [MemoryCategory.Instruction];
    public int MaxRetries { get; init; } = 2;
    public int MaxMergePasses { get; init; } = 3;
}

// A night's dreaming walks every household and its people (the accounts service's people view), and
// dreams for each person on their household's key, counting what it does under the household: there
// is no call behind it to name one, so the household travels with the walk.
public class MemoryDreamingService(
    IMemoryStore store,
    IMemoryConsolidator consolidator,
    IEmbeddingService embeddingService,
    IMetricsPublisher metricsPublisher,
    ICronValidator cronValidator,
    ILogger<MemoryDreamingService> logger,
    MemoryDreamingOptions options,
    IHouseholdConfiguration households,
    IAgentDefinitionProvider agents) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var next = cronValidator.GetNextOccurrence(options.CronSchedule, DateTimeOffset.UtcNow, TimeZoneInfo.Utc);
            if (next is null)
            {
                logger.LogWarning("Cron schedule '{Schedule}' returned no next occurrence, stopping", options.CronSchedule);
                return;
            }

            var delay = next.Value - DateTime.UtcNow;
            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay, ct);
            }

            await RunDreamingAsync(ct);
        }
    }

    private Task RunDreamingAsync(CancellationToken ct) => RunDreamingAsync(DateTimeOffset.UtcNow, ct);

    // Each household's people the store keeps memory for, on the household's key. A household that
    // cannot pay — its own, with no key set — or cannot be read just now is not dreamt for tonight, nor
    // is one the operator suspended, nor a member an owner disabled: the view read for the key says
    // both, as it does for the household's turns. A person the store names who is in no household is
    // nobody's to pay for, so is left alone. Whoever is skipped keeps their memories as they are.
    public async Task RunDreamingAsync(DateTimeOffset now, CancellationToken ct)
    {
        var userIds = (await store.GetAllUserIdsAsync(ct)).ToHashSet(StringComparer.Ordinal);
        if (await WalkAsync(ct) is not { } walk)
        {
            return;
        }

        logger.LogInformation("Starting dreaming cycle for {UserCount} users in {HouseholdCount} households",
            userIds.Count, walk.Count);

        foreach (var household in walk)
        {
            var people = household.People.Where(userIds.Contains).ToList();
            if (people.Count == 0 || await DreamableAsync(household.HouseholdId, ct) is not { } dreamable)
            {
                continue;
            }

            foreach (var userId in people.Where(p => !dreamable.Disabled.Contains(p)))
            {
                try
                {
                    await RunDreamingForUserAsync(userId, now, dreamable.Payer, ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogError(ex, "Dreaming failed for user {UserId}", userId);
                }
            }
        }

        var inNoHousehold = userIds.Except(walk.SelectMany(h => h.People), StringComparer.Ordinal).Count();
        if (inNoHousehold > 0)
        {
            logger.LogWarning("Dreaming left {Count} users alone: they are in no household, so nobody pays for them", inNoHousehold);
        }
    }

    // Every household and its people, or none tonight: with the accounts service away nobody's household
    // is known, so nobody can be paid for — and an outage at the tick must not stop the agent, which a
    // throw out of a hosted service does.
    private async Task<IReadOnlyList<HouseholdMembers>?> WalkAsync(CancellationToken ct)
    {
        try
        {
            return await households.PeopleAsync(ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or HouseholdConfigRefusedException
                                       or TaskCanceledException && !ct.IsCancellationRequested)
        {
            logger.LogError(ex, "Could not read the households and their people; nobody is dreamt for tonight");
            metricsPublisher.Publish(new ErrorEvent
            {
                Service = "memory",
                ErrorType = ex.GetType().Name,
                Message = "Dreaming skipped: the households and their people could not be read"
            });
            return null;
        }
    }

    // Who pays for the household's dreaming and whom of it not to dream for, or null for a household
    // not dreamt for tonight.
    private async Task<(ModelPayer Payer, IReadOnlyList<string> Disabled)?> DreamableAsync(string householdId, CancellationToken ct)
    {
        try
        {
            switch (await agents.ForHouseholdAsync(householdId, ct))
            {
                case { Suspended: true }:
                    logger.LogInformation("Household {Household} is suspended; its people are not dreamt for", householdId);
                    return null;
                case { Payer: { } payer } household:
                    return (payer with { TenantId = householdId }, household.Configuration?.DisabledPeople ?? []);
                default:
                    logger.LogWarning("Household {Household} has set no OpenRouter key; its people are not dreamt for", householdId);
                    return null;
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or HouseholdConfigRefusedException
                                       or TaskCanceledException && !ct.IsCancellationRequested)
        {
            logger.LogError(ex, "Could not read household {Household}; its people are not dreamt for tonight", householdId);
            return null;
        }
    }

    public async Task RunDreamingForUserAsync(string userId, DateTimeOffset now, ModelPayer payer, CancellationToken ct)
    {
        var metrics = metricsPublisher.ForHousehold(payer.TenantId);
        var activeMemories = await GetActiveMemoriesAsync(userId, ct);
        if (activeMemories.Count == 0)
        {
            await RemoveOrphanedProfileAsync(userId, metrics, ct);
            return;
        }

        var mergedCount = 0;
        var refused = new List<IReadOnlyList<string>>();
        for (var pass = 0; pass < options.MaxMergePasses; pass++)
        {
            var (passMerges, passRefusals) = await MergeAsync(userId, activeMemories, payer, metrics, ct);
            refused.AddRange(passRefusals);
            if (passMerges == 0)
            {
                break;
            }

            mergedCount += passMerges;
            activeMemories = await GetActiveMemoriesAsync(userId, ct);
        }

        var decayedCount = await DecayAsync(userId, activeMemories, now, ct);

        var profile = await consolidator.SynthesizeProfileAsync(userId, activeMemories, payer, ct);
        await store.SaveProfileAsync(profile, ct);

        metrics.Publish(new MemoryDreamingEvent
        {
            MergedCount = mergedCount,
            DecayedCount = decayedCount,
            ProfileRegenerated = true,
            UserId = userId,
            RefusedMerges = refused
        });

        logger.LogInformation(
            "Dreaming complete for {UserId}: {Merged} merged, {Refused} refused, {Decayed} decayed, profile regenerated",
            userId, mergedCount, refused.Count, decayedCount);
    }

    private async Task RemoveOrphanedProfileAsync(string userId, IMetricsPublisher metrics, CancellationToken ct)
    {
        var removed = await store.DeleteProfileAsync(userId, ct);

        metrics.Publish(new MemoryDreamingEvent
        {
            MergedCount = 0,
            DecayedCount = 0,
            ProfileRegenerated = false,
            ProfileRemoved = removed,
            UserId = userId
        });

        logger.LogInformation(
            "Dreaming found no memories for {UserId}: profile {Outcome}",
            userId, removed ? "removed" : "absent");
    }

    // What a pass applied, and the merges it refused by the source ids the model named.
    private async Task<(int Merged, IReadOnlyList<IReadOnlyList<string>> Refused)> MergeAsync(
        string userId, IReadOnlyList<MemoryEntry> activeMemories, ModelPayer payer, IMetricsPublisher metrics,
        CancellationToken ct)
    {
        var consolidation = await consolidator.ConsolidateAsync(activeMemories, payer, ct);
        var refused = new List<IReadOnlyList<string>>();

        // Keyed without case, and resolving to the id the store holds rather than the one that came
        // back. A decision's ids went out to a language model and came back retyped: mostly
        // verbatim, sometimes "Mem_3" for "mem_3" — the right memory, the wrong shape. Matched
        // exactly, both ids of a pair miss at once, the decision falls under the two-source guard
        // below, and the pass consolidates less than the model decided with nothing said. Ids are
        // mem_{Guid:N}, so no two can differ by case alone and there is nothing to lose here.
        //
        // The value matters as much as the comparer: what is kept is the stored id, because these
        // go on to DeleteAsync, which is a lookup by key — handed the model's spelling it would
        // remove nothing, leaving the sources of a merge alive beside the memory that replaced them.
        var storedIds = activeMemories
            .DistinctBy(m => m.Id, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(m => m.Id, m => m.Id, StringComparer.OrdinalIgnoreCase);
        var mergedCount = 0;

        foreach (var decision in consolidation.Decisions)
        {
            var validSourceIds = decision.SourceIds
                .Select(id => storedIds.GetValueOrDefault(id))
                .OfType<string>()
                .Distinct(StringComparer.Ordinal)
                .ToList();

            // An id that names no memory is still dropped — a model can invent one, and ignoring
            // case is not the same as accepting anything. Saying which one is what stops the next
            // mismatch of this kind from going unnoticed for as long as the casing did.
            var unplaceable = decision.SourceIds.Where(id => !storedIds.ContainsKey(id)).ToList();
            if (unplaceable.Count > 0)
            {
                logger.LogWarning(
                    "Dreaming for {UserId}: consolidation named {UnknownIds}, which no active memory holds; "
                    + "{Action} keeps {ValidCount} of {SourceCount} sources",
                    userId, string.Join(", ", unplaceable), decision.Action,
                    validSourceIds.Count, decision.SourceIds.Count);
            }

            if (decision.Action is not (MergeAction.Merge or MergeAction.SupersedeOlder) || validSourceIds.Count < 2)
            {
                continue;
            }

            // The refusals. Sources the pair judgment did not link never reach the merge model
            // together, so a decision spanning two groups is one the model made up; and a merge
            // that came back with no text would replace what it was given with nothing.
            if (!consolidation.Permits(validSourceIds))
            {
                logger.LogWarning(
                    "Dreaming for {UserId}: refused {Action} of {SourceIds}, which are not linked by the pair judgment",
                    userId, decision.Action, string.Join(", ", validSourceIds));
                refused.Add(validSourceIds);
                continue;
            }

            if (decision.Action == MergeAction.Merge && string.IsNullOrWhiteSpace(decision.MergedContent))
            {
                logger.LogWarning(
                    "Dreaming for {UserId}: refused a merge of {SourceIds} that came back with no text",
                    userId, string.Join(", ", validSourceIds));
                refused.Add(validSourceIds);
                continue;
            }

            switch (decision.Action)
            {
                case MergeAction.Merge:
                    await ApplyMergeAsync(userId, decision with { SourceIds = validSourceIds }, metrics, ct);
                    mergedCount++;
                    break;

                case MergeAction.SupersedeOlder:
                    await store.DeleteAsync(userId, validSourceIds[0], ct);
                    mergedCount++;
                    break;
            }
        }

        return (mergedCount, refused);
    }

    private async Task<IReadOnlyList<MemoryEntry>> GetActiveMemoriesAsync(string userId, CancellationToken ct)
    {
        return await store.GetByUserIdAsync(userId, ct);
    }

    private async Task ApplyMergeAsync(string userId, MergeDecision decision, IMetricsPublisher metrics, CancellationToken ct)
    {
        var embedding = await embeddingService.GenerateEmbeddingAsync(decision.MergedContent!, ct);
        metrics.RecordEmbedded(1);

        var merged = new MemoryEntry
        {
            Id = $"mem_{Guid.NewGuid():N}",
            UserId = userId,
            Category = decision.Category ?? MemoryCategory.Fact,
            Content = decision.MergedContent!,
            Importance = decision.Importance ?? 0.5,
            Confidence = 0.9,
            Embedding = embedding,
            Tags = decision.Tags ?? [],
            CreatedAt = DateTimeOffset.UtcNow,
            LastAccessedAt = DateTimeOffset.UtcNow
        };

        var stored = await store.StoreAsync(merged, ct);

        foreach (var sourceId in decision.SourceIds)
        {
            await store.DeleteAsync(userId, sourceId, ct);
        }
    }

    private async Task<int> DecayAsync(
        string userId, IReadOnlyList<MemoryEntry> activeMemories, DateTimeOffset now, CancellationToken ct)
    {
        var cutoff = now.AddDays(-options.DecayDays);

        var eligible = activeMemories.Where(m =>
            m.LastAccessedAt < cutoff &&
            !options.DecayExemptCategories.Contains(m.Category) &&
            m.Importance * options.DecayFactor >= options.DecayFloor);

        var decayedCount = 0;
        foreach (var memory in eligible)
        {
            var newImportance = Math.Round(memory.Importance * options.DecayFactor, 2);
            await store.UpdateImportanceAsync(userId, memory.Id, newImportance, ct);
            decayedCount++;
        }

        return decayedCount;
    }
}