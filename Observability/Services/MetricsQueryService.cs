using System.Text.Json;
using Domain.DTOs.Metrics;
using Domain.DTOs.Metrics.Enums;
using JetBrains.Annotations;
using StackExchange.Redis;

namespace Observability.Services;

[UsedImplicitly]
public record ServiceHealthResult(string Service, bool IsHealthy, string LastSeen);

public record MetricsSummary(
    long InputTokens,
    long OutputTokens,
    long TotalTokens,
    decimal Cost,
    long ToolCalls,
    long ToolErrors,
    long TotalRecalls = 0,
    long TotalExtractions = 0,
    long TotalDreamings = 0,
    long MemoriesStored = 0,
    long MemoriesMerged = 0,
    long MemoriesDecayed = 0);

public sealed class MetricsQueryService(IConnectionMultiplexer redis, TimeProvider? timeProvider = null)
{
    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    public async Task<MetricsSummary> GetSummaryAsync(DateOnly from, DateOnly to)
    {
        var db = redis.GetDatabase();
        long inputTokens = 0, outputTokens = 0, costFixed = 0, toolCalls = 0, toolErrors = 0;
        long recalls = 0, extractions = 0, dreamings = 0, memoriesStored = 0, memoriesMerged = 0, memoriesDecayed = 0;

        foreach (var date in EnumerateDates(from, to))
        {
            var key = $"metrics:totals:{date:yyyy-MM-dd}";
            var entries = await db.HashGetAllAsync(key);
            foreach (var entry in entries)
            {
                var field = entry.Name.ToString();
                var value = (long)entry.Value;
                switch (field)
                {
                    case "tokens:input":
                        inputTokens += value;
                        break;
                    case "tokens:output":
                        outputTokens += value;
                        break;
                    case "tokens:cost":
                        costFixed += value;
                        break;
                    case "tools:count":
                        toolCalls += value;
                        break;
                    case "tools:errors":
                        toolErrors += value;
                        break;
                    case "memory:recalls":
                        recalls += value;
                        break;
                    case "memory:extractions":
                        extractions += value;
                        break;
                    case "memory:dreamings":
                        dreamings += value;
                        break;
                    case "memory:stored":
                        memoriesStored += value;
                        break;
                    case "memory:merged":
                        memoriesMerged += value;
                        break;
                    case "memory:decayed":
                        memoriesDecayed += value;
                        break;
                }
            }
        }

        return new MetricsSummary(
            inputTokens,
            outputTokens,
            inputTokens + outputTokens,
            costFixed / 10000m,
            toolCalls,
            toolErrors,
            recalls,
            extractions,
            dreamings,
            memoriesStored,
            memoriesMerged,
            memoriesDecayed);
    }

    public async Task<IReadOnlyList<T>> GetEventsAsync<T>(string keyPrefix, DateOnly from, DateOnly to)
        where T : MetricEvent
    {
        var db = redis.GetDatabase();
        var results = new List<T>();

        foreach (var date in EnumerateDates(from, to))
        {
            var key = $"{keyPrefix}{date:yyyy-MM-dd}";
            var entries = await db.SortedSetRangeByScoreAsync(key);
            results.AddRange(entries
                .Select(e => JsonSerializer.Deserialize<MetricEvent>(e.ToString(), _jsonOptions))
                .OfType<T>());
        }

        return results;
    }

    public async Task<IReadOnlyList<ErrorEvent>> GetRecentErrorsAsync(int limit = 100)
    {
        var db = redis.GetDatabase();
        var entries = await db.ListRangeAsync("metrics:errors:recent", 0, limit - 1);

        return entries
            .Select(e => JsonSerializer.Deserialize<MetricEvent>(e.ToString(), _jsonOptions))
            .OfType<ErrorEvent>()
            .ToList();
    }

    public async Task<IReadOnlyList<ServiceHealthResult>> GetHealthAsync()
    {
        var db = redis.GetDatabase();
        var knownServices = await ServiceHealthRegistry.ListAsync(db, _time.GetUtcNow());

        var tasks = knownServices.Select(async service =>
        {
            var value = await db.StringGetAsync($"metrics:health:{service}");
            var isHealthy = value.HasValue;
            return new ServiceHealthResult(service, isHealthy, isHealthy ? value.ToString() : "N/A");
        });

        return (await Task.WhenAll(tasks)).ToList();
    }

    public async Task<Dictionary<string, long>> GetTokenBreakdownAsync(
        string prefix, DateOnly from, DateOnly to)
    {
        var db = redis.GetDatabase();
        var breakdown = new Dictionary<string, long>();

        foreach (var date in EnumerateDates(from, to))
        {
            var key = $"metrics:totals:{date:yyyy-MM-dd}";
            var entries = await db.HashGetAllAsync(key);
            foreach (var entry in entries.Where(e => e.Name.ToString().StartsWith(prefix)))
            {
                var name = entry.Name.ToString()[prefix.Length..];
                var value = (long)entry.Value;
                if (!breakdown.TryAdd(name, value))
                {
                    breakdown[name] += value;
                }
            }
        }

        return breakdown;
    }

    public async Task<Dictionary<string, decimal>> GetTokenGroupedAsync(
        TokenDimension dimension, TokenMetric metric, DateOnly from, DateOnly to)
    {
        return metric switch
        {
            TokenMetric.Tokens or TokenMetric.Cost
                => await GroupTokenUsageAsync(dimension, metric, from, to),
            TokenMetric.TruncationCount or TokenMetric.MessagesDropped or TokenMetric.TokensTrimmed
                => await GroupTruncationsAsync(dimension, metric, from, to),
            _ => throw new ArgumentOutOfRangeException(nameof(metric))
        };
    }

    private async Task<Dictionary<string, decimal>> GroupTokenUsageAsync(
        TokenDimension dimension, TokenMetric metric, DateOnly from, DateOnly to)
    {
        var events = await GetEventsAsync<TokenUsageEvent>("metrics:tokens:", from, to);
        return events
            .GroupBy(e => dimension switch
            {
                TokenDimension.User => e.Sender,
                TokenDimension.Model => e.Model,
                TokenDimension.Agent => e.AgentId ?? "unknown",
                _ => throw new ArgumentOutOfRangeException(nameof(dimension))
            })
            .ToDictionary(
                g => g.Key,
                g => metric switch
                {
                    TokenMetric.Tokens => g.Sum(e => (decimal)(e.InputTokens + e.OutputTokens)),
                    TokenMetric.Cost => g.Sum(e => e.Cost),
                    _ => throw new ArgumentOutOfRangeException(nameof(metric))
                });
    }

    private async Task<Dictionary<string, decimal>> GroupTruncationsAsync(
        TokenDimension dimension, TokenMetric metric, DateOnly from, DateOnly to)
    {
        var events = await GetEventsAsync<ContextTruncationEvent>("metrics:truncations:", from, to);
        return events
            .GroupBy(e => dimension switch
            {
                TokenDimension.User => e.Sender,
                TokenDimension.Model => e.Model,
                TokenDimension.Agent => e.AgentId ?? "unknown",
                _ => throw new ArgumentOutOfRangeException(nameof(dimension))
            })
            .ToDictionary(
                g => g.Key,
                g => metric switch
                {
                    TokenMetric.TruncationCount => (decimal)g.Count(),
                    TokenMetric.MessagesDropped => g.Sum(e => (decimal)e.DroppedMessages),
                    TokenMetric.TokensTrimmed => g.Sum(e => (decimal)(e.EstimatedTokensBefore - e.EstimatedTokensAfter)),
                    _ => throw new ArgumentOutOfRangeException(nameof(metric))
                });
    }

    public async Task<Dictionary<string, decimal>> GetToolGroupedAsync(
        ToolDimension dimension, ToolMetric metric, DateOnly from, DateOnly to)
    {
        var events = await GetEventsAsync<ToolCallEvent>("metrics:tools:", from, to);
        return events
            .GroupBy(e => dimension switch
            {
                ToolDimension.ToolName => e.ToolName,
                ToolDimension.Status => e.Success ? "Success" : "Failure",
                _ => throw new ArgumentOutOfRangeException(nameof(dimension))
            })
            .ToDictionary(
                g => g.Key,
                g => metric switch
                {
                    ToolMetric.CallCount => g.Count(),
                    ToolMetric.AvgDuration => (decimal)g.Average(e => e.DurationMs),
                    ToolMetric.ErrorRate => g.Any()
                        ? (decimal)g.Count(e => !e.Success) / g.Count() * 100m
                        : 0m,
                    _ => throw new ArgumentOutOfRangeException(nameof(metric))
                });
    }

    public async Task<Dictionary<string, int>> GetErrorGroupedAsync(
        ErrorDimension dimension, DateOnly from, DateOnly to)
    {
        var events = await GetEventsAsync<ErrorEvent>("metrics:errors:", from, to);
        return events
            .GroupBy(e => dimension switch
            {
                ErrorDimension.Service => e.Service,
                ErrorDimension.ErrorType => e.ErrorType,
                _ => throw new ArgumentOutOfRangeException(nameof(dimension))
            })
            .ToDictionary(g => g.Key, g => g.Count());
    }

    public async Task<Dictionary<string, decimal>> GetMemoryGroupedAsync(
        MemoryDimension dimension, MemoryMetric metric, DateOnly from, DateOnly to)
    {
        var recalls = await GetEventsAsync<MemoryRecallEvent>("metrics:memory-recall:", from, to);
        var extractions = await GetEventsAsync<MemoryExtractionEvent>("metrics:memory-extraction:", from, to);
        var dreamings = await GetEventsAsync<MemoryDreamingEvent>("metrics:memory-dreaming:", from, to);
        var judgments = await GetEventsAsync<MemoryJudgmentEvent>("metrics:memory-judgment:", from, to);

        // The outcome is an extraction's, so under it the share is over extractions alone.
        var allEvents = dimension == MemoryDimension.Outcome
            ? extractions.Cast<MetricEvent>().ToList()
            : recalls.Cast<MetricEvent>()
                .Concat(extractions)
                .Concat(dreamings)
                .Concat(judgments)
                .ToList();

        return allEvents
            .GroupBy(e => dimension switch
            {
                MemoryDimension.User => e switch
                {
                    MemoryRecallEvent r => r.UserId,
                    MemoryExtractionEvent x => x.UserId,
                    MemoryDreamingEvent d => d.UserId,
                    MemoryJudgmentEvent j => j.UserId,
                    _ => "unknown"
                },
                MemoryDimension.EventType => e switch
                {
                    MemoryRecallEvent => "Recall",
                    MemoryExtractionEvent => "Extraction",
                    MemoryDreamingEvent => "Dreaming",
                    MemoryJudgmentEvent => "Judgment",
                    _ => "unknown"
                },
                MemoryDimension.Agent => e.AgentId ?? "unknown",
                MemoryDimension.Outcome => (e as MemoryExtractionEvent)?.Outcome ?? "(unrecorded)",
                _ => throw new ArgumentOutOfRangeException(nameof(dimension))
            })
            .ToDictionary(
                g => g.Key,
                g => metric switch
                {
                    MemoryMetric.Count => (decimal)g.Count(),
                    MemoryMetric.AvgDuration => g.Any(e => e is MemoryRecallEvent or MemoryExtractionEvent)
                        ? (decimal)g.Where(e => e is MemoryRecallEvent or MemoryExtractionEvent)
                            .Average(e => e switch
                            {
                                MemoryRecallEvent r => r.DurationMs,
                                MemoryExtractionEvent x => x.DurationMs,
                                _ => 0
                            })
                        : 0m,
                    MemoryMetric.StoredCount => g.OfType<MemoryExtractionEvent>().Sum(e => (decimal)e.StoredCount),
                    MemoryMetric.MergedCount => g.OfType<MemoryDreamingEvent>().Sum(e => (decimal)e.MergedCount),
                    MemoryMetric.DecayedCount => g.OfType<MemoryDreamingEvent>().Sum(e => (decimal)e.DecayedCount),
                    MemoryMetric.CandidateCount => g.OfType<MemoryExtractionEvent>().Sum(e => (decimal)e.CandidateCount),
                    MemoryMetric.DroppedCount => g.OfType<MemoryExtractionEvent>().Sum(e => (decimal)e.DroppedCount),
                    MemoryMetric.RefusedMergeCount => g.OfType<MemoryDreamingEvent>().Sum(e => (decimal)e.RefusedCount),
                    _ => throw new ArgumentOutOfRangeException(nameof(metric))
                });
    }

    public static decimal ComputePercentile(IEnumerable<decimal> values, decimal q)
    {
        var sorted = values.OrderBy(v => v).ToArray();
        if (sorted.Length == 0)
        {
            return 0m;
        }

        var rank = (int)Math.Ceiling((double)q / 100.0 * sorted.Length);
        var index = Math.Clamp(rank - 1, 0, sorted.Length - 1);
        return sorted[index];
    }

    internal static decimal AggregateLatency(IEnumerable<decimal> values, Aggregation aggregation)
    {
        var list = values.ToArray();
        if (list.Length == 0)
        {
            return 0m;
        }

        return aggregation switch
        {
            Aggregation.Avg => Math.Round(list.Average(), 2),
            Aggregation.P50 => ComputePercentile(list, 50),
            Aggregation.P95 => ComputePercentile(list, 95),
            Aggregation.P99 => ComputePercentile(list, 99),
            Aggregation.Count => list.Length,
            Aggregation.Max => list.Max(),
            _ => throw new ArgumentOutOfRangeException(nameof(aggregation))
        };
    }

    public async Task<Dictionary<string, decimal>> GetLatencyGroupedAsync(
        LatencyDimension dimension, Aggregation aggregation, DateOnly from, DateOnly to)
    {
        var events = await GetEventsAsync<LatencyEvent>("metrics:latency:", from, to);
        return events
            .GroupBy(e => dimension switch
            {
                LatencyDimension.Stage => e.Stage.ToString(),
                LatencyDimension.Agent => e.AgentId ?? "unknown",
                LatencyDimension.Model => e.Model ?? "unknown",
                _ => throw new ArgumentOutOfRangeException(nameof(dimension))
            })
            .ToDictionary(
                g => g.Key,
                g => AggregateLatency(g.Select(e => (decimal)e.DurationMs), aggregation));
    }

    public async Task<IReadOnlyList<LatencyTrendSeries>> GetLatencyTrendAsync(
        Aggregation aggregation, DateOnly from, DateOnly to)
    {
        var events = await GetEventsAsync<LatencyEvent>("metrics:latency:", from, to);
        var hourly = to.DayNumber - from.DayNumber <= 2;

        return events
            .GroupBy(e => e.Stage)
            .OrderBy(g => g.Key)
            .Select(stageGroup => new LatencyTrendSeries(
                stageGroup.Key.ToString(),
                stageGroup
                    .GroupBy(e => BucketTimestamp(e.Timestamp, hourly))
                    .OrderBy(b => b.Key)
                    .Select(b => new LatencyTrendPoint(
                        b.Key,
                        AggregateLatency(b.Select(e => (decimal)e.DurationMs), aggregation)))
                    .ToList()))
            .ToList();
    }

    private static DateTimeOffset BucketTimestamp(DateTimeOffset ts, bool hourly)
    {
        var u = ts.UtcDateTime;
        return hourly
            ? new DateTimeOffset(u.Year, u.Month, u.Day, u.Hour, 0, 0, TimeSpan.Zero)
            : new DateTimeOffset(u.Year, u.Month, u.Day, 0, 0, 0, TimeSpan.Zero);
    }

    public async Task<Dictionary<string, decimal>> GetVoiceGroupedAsync(
        VoiceDimension dimension,
        VoiceMetric metric,
        DateOnly from,
        DateOnly to,
        Aggregation aggregation = Aggregation.Avg)
    {
        var events = await GetEventsAsync<VoiceEvent>("metrics:voice:", from, to);
        var scoped = events.Where(e => e.Metric == metric);

        Func<VoiceEvent, string?> selector = dimension switch
        {
            VoiceDimension.SatelliteId => e => e.SatelliteId,
            VoiceDimension.Room => e => e.Room,
            VoiceDimension.Identity => e => e.Identity,
            VoiceDimension.Outcome => e => e.Outcome,
            VoiceDimension.Priority => e => e.Priority,
            VoiceDimension.Speaker => e => e.Speaker,
            VoiceDimension.Channel => e => e.Channel,
            _ => e => e.SatelliteId
        };

        // Duration metrics are identified by their name suffix rather than an explicit list: the
        // list silently degraded every newly added ...Ms member to a count.
        var isDuration = metric.ToString().EndsWith("Ms", StringComparison.Ordinal);

        return scoped
            .GroupBy(e => selector(e) ?? "(unknown)")
            .ToDictionary(
                g => g.Key,
                g => isDuration
                    ? AggregateLatency(g.Select(e => (decimal)(e.DurationMs ?? 0)), aggregation)
                    : (decimal)g.Count());
    }

    public async Task<Dictionary<string, decimal>> GetSkillPreloadGroupedAsync(
        SkillPreloadDimension dimension,
        SkillPreloadMetric metric,
        DateOnly from,
        DateOnly to,
        Aggregation aggregation = Aggregation.Avg)
    {
        var events = await GetEventsAsync<SkillPreloadEvent>("metrics:skills:", from, to);

        // By skill, an event counts once per skill it preloaded and not at all when it preloaded
        // none: the question that dimension answers is "which skills arrive early".
        var keyed = dimension == SkillPreloadDimension.Skill
            ? events.SelectMany(e => e.Skills.Select(skill => (Key: skill, Event: e)))
            : events.Select(e => (Key: dimension switch
            {
                SkillPreloadDimension.Outcome => e.Outcome,
                SkillPreloadDimension.Agent => e.AgentId ?? "(unknown)",
                SkillPreloadDimension.Channel => e.Channel ?? "(unknown)",
                _ => throw new ArgumentOutOfRangeException(nameof(dimension))
            }, Event: e));

        return keyed
            .GroupBy(k => k.Key)
            .ToDictionary(
                g => g.Key,
                g => metric switch
                {
                    SkillPreloadMetric.Count => (decimal)g.Count(),
                    SkillPreloadMetric.LatencyMs => AggregateLatency(
                        g.Where(k => k.Event.DurationMs is not null).Select(k => (decimal)k.Event.DurationMs!.Value), aggregation),
                    SkillPreloadMetric.InputTokens => g.Sum(k => (decimal)(k.Event.InputTokens ?? 0)),
                    _ => throw new ArgumentOutOfRangeException(nameof(metric))
                });
    }

    // One series per outcome, a count per bucket: what "the preload rate over time" is read off.
    public async Task<IReadOnlyList<LatencyTrendSeries>> GetSkillPreloadTrendAsync(DateOnly from, DateOnly to)
    {
        var events = await GetEventsAsync<SkillPreloadEvent>("metrics:skills:", from, to);
        var hourly = to.DayNumber - from.DayNumber <= 2;

        return events
            .GroupBy(e => e.Outcome)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(outcome => new LatencyTrendSeries(
                outcome.Key,
                outcome
                    .GroupBy(e => BucketTimestamp(e.Timestamp, hourly))
                    .OrderBy(b => b.Key)
                    .Select(b => new LatencyTrendPoint(b.Key, b.Count()))
                    .ToList()))
            .ToList();
    }

    public async Task<Dictionary<string, int>> GetScheduleGroupedAsync(
        ScheduleDimension dimension, DateOnly from, DateOnly to)
    {
        var events = await GetEventsAsync<ScheduleExecutionEvent>("metrics:schedules:", from, to);
        return events
            .GroupBy(e => dimension switch
            {
                ScheduleDimension.Schedule => e.ScheduleId,
                ScheduleDimension.Status => e.Success ? "Success" : "Failure",
                _ => throw new ArgumentOutOfRangeException(nameof(dimension))
            })
            .ToDictionary(g => g.Key, g => g.Count());
    }

    private static IEnumerable<DateOnly> EnumerateDates(DateOnly from, DateOnly to)
    {
        for (var date = from; date <= to; date = date.AddDays(1))
        {
            yield return date;
        }
    }
}