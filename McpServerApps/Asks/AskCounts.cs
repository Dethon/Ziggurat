using System.Collections.Concurrent;
using System.Text.Json;
using McpServerApps.Store;

namespace McpServerApps.Asks;

// How many asks each app was sent over the last 24 hours, for its daily cap: one count per app, whoever
// viewed it (published and preview are one app). Kept beside the repository (BuiltAppStore.AskCountsFile),
// so a restart lifts no cap, a deleted app takes its counts along, and a publish, the preview and the quota
// never see them.
//
// Counted by the minute an ask was sent in, and a minute stops counting a day after it began: an ask counts
// for a day less at most a minute, and the file holds at most a day's minutes however much is asked. Each
// app's file is read and rewritten one ask at a time, so two asks never both take the last slot.
public sealed class AskCounts(BuiltAppStore store, TimeProvider clock)
{
    public static readonly TimeSpan Window = TimeSpan.FromDays(1);

    private static readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _apps = new(StringComparer.Ordinal);

    private sealed record Minute(DateTimeOffset Start, int Asks);

    // Counts the ask now unless the app has sent its limit already.
    public Task<AskCount> TryCountAsync(BuiltApp app, int limit, CancellationToken ct) =>
        OneAtATimeAsync<AskCount>(app, ct, async () =>
        {
            var now = clock.GetUtcNow();
            if (await CountedAsync(app, now, ct) is not { } counted)
            {
                return new AskCount.Gone();
            }

            var total = counted.Sum(minute => minute.Asks);
            if (total >= limit)
            {
                return new AskCount.Capped(limit, FreesAt(counted, total, limit));
            }

            var start = new DateTimeOffset(now.UtcTicks - now.UtcTicks % TimeSpan.TicksPerMinute, TimeSpan.Zero);
            var asks = counted.FirstOrDefault(minute => minute.Start == start)?.Asks ?? 0;
            return await SaveAsync(app, [.. counted.Where(minute => minute.Start != start), new Minute(start, asks + 1)], ct)
                ? new AskCount.Counted(start)
                : new AskCount.Gone();
        });

    // An ask counted and then never delivered gives its slot back, from the minute it was counted in.
    public Task ReleaseAsync(BuiltApp app, AskCount.Counted counted) =>
        // Never cancelled: a slot the page's leaving kept would be a slot the household never asked with.
        OneAtATimeAsync<AskCount>(app, CancellationToken.None, async () =>
        {
            if (await CountedAsync(app, clock.GetUtcNow(), CancellationToken.None) is { } kept)
            {
                await SaveAsync(app, [.. kept
                    .Select(minute => minute.Start == counted.Minute ? minute with { Asks = minute.Asks - 1 } : minute)
                    .Where(minute => minute.Asks > 0)], CancellationToken.None);
            }

            return counted;
        });

    // When the oldest minutes have rolled out far enough to bring the count under the limit; never, for a
    // limit of none.
    private static DateTimeOffset? FreesAt(IReadOnlyList<Minute> counted, int total, int limit)
    {
        if (limit <= 0)
        {
            return null;
        }

        var ordered = counted.OrderBy(minute => minute.Start).ToList();
        return ordered
            .Where((_, i) => total - ordered.Take(i + 1).Sum(minute => minute.Asks) < limit)
            .Select(minute => minute.Start + Window)
            .First();
    }

    // The minutes still counting, or null when the app is gone.
    private async Task<IReadOnlyList<Minute>?> CountedAsync(BuiltApp app, DateTimeOffset now, CancellationToken ct)
    {
        var file = store.AskCountsFile(app);
        try
        {
            await using var stream = File.OpenRead(file);
            var minutes = await JsonSerializer.DeserializeAsync<List<Minute>>(stream, _json, ct) ?? [];
            return [.. minutes.Where(minute => now - minute.Start < Window)];
        }
        catch (FileNotFoundException)
        {
            return [];
        }
        catch (DirectoryNotFoundException)
        {
            return null;
        }
    }

    // False when the app was deleted meanwhile: its directory is never made again.
    private async Task<bool> SaveAsync(BuiltApp app, IReadOnlyList<Minute> minutes, CancellationToken ct)
    {
        var file = store.AskCountsFile(app);
        var temp = file + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(minutes.OrderBy(minute => minute.Start), _json), ct);
            File.Move(temp, file, overwrite: true);
            return true;
        }
        catch (DirectoryNotFoundException)
        {
            return false;
        }
    }

    private async Task<T> OneAtATimeAsync<T>(BuiltApp app, CancellationToken ct, Func<Task<T>> work)
    {
        var gate = _apps.GetOrAdd(app.Id, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            return await work();
        }
        finally
        {
            gate.Release();
        }
    }
}

// What counting an ask came to: counted (in the minute named, which a release gives back), the app at its
// cap (FreesAt: when an ask is counted again, null for never), or the app deleted meanwhile.
public abstract record AskCount
{
    public sealed record Counted(DateTimeOffset Minute) : AskCount;

    public sealed record Capped(int Limit, DateTimeOffset? FreesAt) : AskCount;

    public sealed record Gone : AskCount;
}