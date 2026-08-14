using Domain.Contracts;
using Domain.DTOs;
using Domain.DTOs.Metrics;
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

public class MemoryDreamingService(
    IMemoryStore store,
    IMemoryConsolidator consolidator,
    IEmbeddingService embeddingService,
    IMetricsPublisher metricsPublisher,
    ICronValidator cronValidator,
    ILogger<MemoryDreamingService> logger,
    MemoryDreamingOptions options) : BackgroundService
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

    private async Task RunDreamingAsync(CancellationToken ct)
    {
        var userIds = await store.GetAllUserIdsAsync(ct);
        logger.LogInformation("Starting dreaming cycle for {UserCount} users", userIds.Count);

        var now = DateTimeOffset.UtcNow;
        foreach (var userId in userIds)
        {
            try
            {
                await RunDreamingForUserAsync(userId, now, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Dreaming failed for user {UserId}", userId);
            }
        }
    }

    public async Task RunDreamingForUserAsync(string userId, DateTimeOffset now, CancellationToken ct)
    {
        var activeMemories = await GetActiveMemoriesAsync(userId, ct);
        if (activeMemories.Count == 0)
        {
            await RemoveOrphanedProfileAsync(userId, ct);
            return;
        }

        var mergedCount = 0;
        for (var pass = 0; pass < options.MaxMergePasses; pass++)
        {
            var passMerges = await MergeAsync(userId, activeMemories, ct);
            if (passMerges == 0)
            {
                break;
            }

            mergedCount += passMerges;
            activeMemories = await GetActiveMemoriesAsync(userId, ct);
        }

        var decayedCount = await DecayAsync(userId, activeMemories, now, ct);

        var profile = await consolidator.SynthesizeProfileAsync(userId, activeMemories, ct);
        await store.SaveProfileAsync(profile, ct);

        metricsPublisher.Publish(new MemoryDreamingEvent
        {
            MergedCount = mergedCount,
            DecayedCount = decayedCount,
            ProfileRegenerated = true,
            UserId = userId
        });

        logger.LogInformation(
            "Dreaming complete for {UserId}: {Merged} merged, {Decayed} decayed, profile regenerated",
            userId, mergedCount, decayedCount);
    }

    private async Task RemoveOrphanedProfileAsync(string userId, CancellationToken ct)
    {
        var removed = await store.DeleteProfileAsync(userId, ct);

        metricsPublisher.Publish(new MemoryDreamingEvent
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

    private async Task<int> MergeAsync(string userId, IReadOnlyList<MemoryEntry> activeMemories, CancellationToken ct)
    {
        var decisions = await consolidator.ConsolidateAsync(activeMemories, ct);

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

        foreach (var decision in decisions)
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

            switch (decision.Action)
            {
                case MergeAction.Merge when validSourceIds.Count >= 2:
                    await ApplyMergeAsync(userId, decision with { SourceIds = validSourceIds }, ct);
                    mergedCount++;
                    break;

                case MergeAction.SupersedeOlder when validSourceIds.Count >= 2:
                    await store.DeleteAsync(userId, validSourceIds[0], ct);
                    mergedCount++;
                    break;
            }
        }

        return mergedCount;
    }

    private async Task<IReadOnlyList<MemoryEntry>> GetActiveMemoriesAsync(string userId, CancellationToken ct)
    {
        return await store.GetByUserIdAsync(userId, ct);
    }

    private async Task ApplyMergeAsync(string userId, MergeDecision decision, CancellationToken ct)
    {
        var embedding = decision.MergedContent is not null
            ? await embeddingService.GenerateEmbeddingAsync(decision.MergedContent, ct)
            : null;

        var merged = new MemoryEntry
        {
            Id = $"mem_{Guid.NewGuid():N}",
            UserId = userId,
            Category = decision.Category ?? MemoryCategory.Fact,
            Content = decision.MergedContent ?? string.Empty,
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