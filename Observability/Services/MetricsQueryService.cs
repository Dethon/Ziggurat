using System.Text.Json;
using Domain.DTOs.Channel;
using Domain.DTOs.Metrics;
using Domain.DTOs.Metrics.Enums;
using Domain.Metrics;
using JetBrains.Annotations;
using StackExchange.Redis;

namespace Observability.Services;

// Household is null for the deployment's own services and a probed container.
[UsedImplicitly]
public record ServiceHealthResult(string Service, bool IsHealthy, string LastSeen, string? Household = null);

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

// Every read takes the household it is for: a household's id, MetricKeys.Deployment for the events no
// household's turn made, or null for everybody's together — what the dashboard showed before
// households, and still shows until it can pick one.
public sealed class MetricsQueryService(IConnectionMultiplexer redis, TimeProvider? timeProvider = null)
{
    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    public async Task<MetricsSummary> GetSummaryAsync(DateOnly from, DateOnly to, string? household = null)
    {
        long inputTokens = 0, outputTokens = 0, costFixed = 0, toolCalls = 0, toolErrors = 0;
        long recalls = 0, extractions = 0, dreamings = 0, memoriesStored = 0, memoriesMerged = 0, memoriesDecayed = 0;

        foreach (var entry in (await TotalsAsync(from, to, household)).SelectMany(t => t.Entries))
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

    // The household an event is read back as is the key it was rolled up under, not anything in its
    // body, so a roll-up the migration moved into the home household reads as the home's.
    public async Task<IReadOnlyList<T>> GetEventsAsync<T>(string family, DateOnly from, DateOnly to, string? household = null)
        where T : MetricEvent
    {
        var db = redis.GetDatabase();
        var results = new List<T>();

        foreach (var scope in await ScopesAsync(db, household))
        {
            foreach (var date in EnumerateDates(from, to))
            {
                var entries = await db.SortedSetRangeByScoreAsync(MetricKeys.Daily(family, scope, date));
                results.AddRange(entries
                    .Select(e => JsonSerializer.Deserialize<MetricEvent>(e.ToString(), _jsonOptions))
                    .OfType<T>()
                    .Select(e => (T)(e with { TenantId = MetricKeys.TenantOf(scope) })));
            }
        }

        return results.OrderBy(e => e.Timestamp).ToList();
    }

    public async Task<IReadOnlyList<ErrorEvent>> GetRecentErrorsAsync(int limit = 100, string? household = null)
    {
        var db = redis.GetDatabase();
        var results = new List<ErrorEvent>();
        foreach (var scope in await ScopesAsync(db, household))
        {
            var entries = await db.ListRangeAsync(MetricKeys.RecentErrors(scope), 0, limit - 1);
            results.AddRange(entries
                .Select(e => JsonSerializer.Deserialize<MetricEvent>(e.ToString(), _jsonOptions))
                .OfType<ErrorEvent>()
                .Select(e => e with { TenantId = MetricKeys.TenantOf(scope) }));
        }

        return results.OrderByDescending(e => e.Timestamp).Take(limit).ToList();
    }

    public async Task<IReadOnlyList<ServiceHealthResult>> GetHealthAsync(string? household = null)
    {
        var db = redis.GetDatabase();
        var roster = (await ServiceHealthRegistry.ListAsync(db, _time.GetUtcNow()))
            .Select(member => (Member: member, Parsed: ServiceHealthRegistry.Parse(member)))
            .Where(m => household is null || MetricKeys.ScopeOf(m.Parsed.TenantId) == MetricKeys.ScopeOf(household));

        var tasks = roster.Select(async m =>
        {
            var value = await db.StringGetAsync(ServiceHealthRegistry.HealthKey(m.Member));
            var isHealthy = value.HasValue;
            return new ServiceHealthResult(
                m.Parsed.Service, isHealthy, isHealthy ? value.ToString() : "N/A", m.Parsed.TenantId);
        });

        return (await Task.WhenAll(tasks)).ToList();
    }

    // Every scope something has been rolled up under, the deployment's own included.
    public async Task<IReadOnlyList<string>> GetHouseholdsAsync() =>
        await ScopesAsync(redis.GetDatabase(), null);

    public async Task<LemonadeUsage> GetLemonadeUsageAsync(DateOnly from, DateOnly to, string? household = null) =>
        Lemonade((await TotalsAsync(from, to, household)).SelectMany(t => t.Entries));

    // Who uses what: every scope's use over the range, keyed by its household id or
    // MetricKeys.Deployment.
    public async Task<Dictionary<string, LemonadeUsage>> GetLemonadeByHouseholdAsync(DateOnly from, DateOnly to) =>
        (await TotalsAsync(from, to, null))
            .GroupBy(t => t.Scope)
            .ToDictionary(g => g.Key, g => Lemonade(g.SelectMany(t => t.Entries)));

    // One household's tokens and cost per agent (labelled as the dashboard labels them) and per
    // person (by account, under the latest name their turns carried), costliest first, beside its
    // Lemonade use: what its owner reads in the admin area.
    public async Task<HouseholdUsage> GetHouseholdUsageAsync(string household, DateOnly from, DateOnly to)
    {
        var events = await GetEventsAsync<TokenUsageEvent>(MetricKeys.Tokens, from, to, household);
        var agent = AgentLabels(events, "unknown");
        var person = PersonLabels(events, e => e.UserId, e => e.Sender);
        return new HouseholdUsage(
            Lines(events, e => (agent(e), null)),
            Lines(events, e => (person(e), e.UserId)),
            await GetLemonadeUsageAsync(from, to, household));
    }

    private static IReadOnlyList<UsageLine> Lines(
        IEnumerable<TokenUsageEvent> events, Func<TokenUsageEvent, (string Name, string? Id)> by) =>
        events
            .GroupBy(by)
            .Select(g => new UsageLine(g.Key.Name, g.Sum(e => (long)e.InputTokens + e.OutputTokens), g.Sum(e => e.Cost), g.Key.Id))
            .OrderByDescending(l => l.Cost)
            .ThenByDescending(l => l.Tokens)
            .ThenBy(l => l.Name, StringComparer.Ordinal)
            .ToList();

    public async Task<Dictionary<string, long>> GetTokenBreakdownAsync(
        string prefix, DateOnly from, DateOnly to, string? household = null)
    {
        return (await TotalsAsync(from, to, household))
            .SelectMany(t => t.Entries)
            .Where(e => e.Name.ToString().StartsWith(prefix))
            .GroupBy(e => e.Name.ToString()[prefix.Length..])
            .ToDictionary(g => g.Key, g => g.Sum(e => (long)e.Value));
    }

    private static LemonadeUsage Lemonade(IEnumerable<HashEntry> entries)
    {
        var totals = entries
            .GroupBy(e => e.Name.ToString())
            .ToDictionary(g => g.Key, g => g.Sum(e => (long)e.Value));
        return new LemonadeUsage(
            totals.GetValueOrDefault(LemonadeTotals.TranscribedMs) / 1000.0,
            totals.GetValueOrDefault(LemonadeTotals.SpokenCharacters),
            totals.GetValueOrDefault(LemonadeTotals.Embeddings));
    }

    private async Task<IReadOnlyList<(string Scope, HashEntry[] Entries)>> TotalsAsync(
        DateOnly from, DateOnly to, string? household)
    {
        var db = redis.GetDatabase();
        var totals = new List<(string, HashEntry[])>();
        foreach (var scope in await ScopesAsync(db, household))
        {
            foreach (var date in EnumerateDates(from, to))
            {
                totals.Add((scope, await db.HashGetAllAsync(MetricKeys.Daily(MetricKeys.Totals, scope, date))));
            }
        }

        return totals;
    }

    private static async Task<IReadOnlyList<string>> ScopesAsync(IDatabase db, string? household) =>
        household is not null
            ? [MetricKeys.ScopeOf(household)]
            : (await db.SetMembersAsync(MetricKeys.Scopes) ?? [])
                .Select(m => m.ToString())
                .Append(MetricKeys.Deployment)
                .Distinct()
                .Order(StringComparer.Ordinal)
                .ToList();

    public async Task<Dictionary<string, decimal>> GetTokenGroupedAsync(
        TokenDimension dimension, TokenMetric metric, DateOnly from, DateOnly to, string? household = null)
    {
        return metric switch
        {
            TokenMetric.Tokens or TokenMetric.Cost
                => await GroupTokenUsageAsync(dimension, metric, from, to, household),
            TokenMetric.TruncationCount or TokenMetric.MessagesDropped or TokenMetric.TokensTrimmed
                => await GroupTruncationsAsync(dimension, metric, from, to, household),
            _ => throw new ArgumentOutOfRangeException(nameof(metric))
        };
    }

    private async Task<Dictionary<string, decimal>> GroupTokenUsageAsync(
        TokenDimension dimension, TokenMetric metric, DateOnly from, DateOnly to, string? household = null)
    {
        var events = await GetEventsAsync<TokenUsageEvent>(MetricKeys.Tokens, from, to, household);
        var agent = AgentLabels(events, "unknown");
        var person = PersonLabels(events, e => e.UserId, e => e.Sender);
        return events
            .GroupBy(e => dimension switch
            {
                TokenDimension.User => person(e),
                TokenDimension.Model => e.Model,
                TokenDimension.Agent => agent(e),
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
        TokenDimension dimension, TokenMetric metric, DateOnly from, DateOnly to, string? household = null)
    {
        var events = await GetEventsAsync<ContextTruncationEvent>(MetricKeys.Truncations, from, to, household);
        var agent = AgentLabels(events, "unknown");
        var person = PersonLabels(events, e => e.UserId, e => e.Sender);
        return events
            .GroupBy(e => dimension switch
            {
                TokenDimension.User => person(e),
                TokenDimension.Model => e.Model,
                TokenDimension.Agent => agent(e),
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
        ToolDimension dimension, ToolMetric metric, DateOnly from, DateOnly to, string? household = null)
    {
        var events = await GetEventsAsync<ToolCallEvent>(MetricKeys.Tools, from, to, household);
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
        ErrorDimension dimension, DateOnly from, DateOnly to, string? household = null)
    {
        var events = await GetEventsAsync<ErrorEvent>(MetricKeys.Errors, from, to, household);
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
        MemoryDimension dimension, MemoryMetric metric, DateOnly from, DateOnly to, string? household = null)
    {
        var recalls = await GetEventsAsync<MemoryRecallEvent>(MetricKeys.MemoryRecall, from, to, household);
        var extractions = await GetEventsAsync<MemoryExtractionEvent>(MetricKeys.MemoryExtraction, from, to, household);
        var dreamings = await GetEventsAsync<MemoryDreamingEvent>(MetricKeys.MemoryDreaming, from, to, household);
        var judgments = await GetEventsAsync<MemoryJudgmentEvent>(MetricKeys.MemoryJudgment, from, to, household);

        // The outcome is an extraction's, so under it the share is over extractions alone.
        var allEvents = dimension == MemoryDimension.Outcome
            ? extractions.Cast<MetricEvent>().ToList()
            : recalls.Cast<MetricEvent>()
                .Concat(extractions)
                .Concat(dreamings)
                .Concat(judgments)
                .ToList();

        var agent = AgentLabels(allEvents, "unknown");

        // Memory's events name only the account; the person's model calls name them too, so the
        // breakdown is under the name the token figures show them by.
        var person = dimension == MemoryDimension.User
            ? AccountNames(await GetEventsAsync<TokenUsageEvent>(MetricKeys.Tokens, from, to, household))
            : _ => null;
        return allEvents
            .GroupBy(e => dimension switch
            {
                MemoryDimension.User => e switch
                {
                    MemoryRecallEvent r => person(r.UserId) ?? r.UserId,
                    MemoryExtractionEvent x => person(x.UserId) ?? x.UserId,
                    MemoryDreamingEvent d => person(d.UserId) ?? d.UserId,
                    MemoryJudgmentEvent j => person(j.UserId) ?? j.UserId,
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
                MemoryDimension.Agent => agent(e),
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
        LatencyDimension dimension, Aggregation aggregation, DateOnly from, DateOnly to, string? household = null)
    {
        var events = await GetEventsAsync<LatencyEvent>(MetricKeys.Latency, from, to, household);
        var agent = AgentLabels(events, "unknown");
        return events
            .GroupBy(e => dimension switch
            {
                LatencyDimension.Stage => e.Stage.ToString(),
                LatencyDimension.Agent => agent(e),
                LatencyDimension.Model => e.Model ?? "unknown",
                _ => throw new ArgumentOutOfRangeException(nameof(dimension))
            })
            .ToDictionary(
                g => g.Key,
                g => AggregateLatency(g.Select(e => (decimal)e.DurationMs), aggregation));
    }

    public async Task<IReadOnlyList<LatencyTrendSeries>> GetLatencyTrendAsync(
        Aggregation aggregation, DateOnly from, DateOnly to, string? household = null)
    {
        var events = await GetEventsAsync<LatencyEvent>(MetricKeys.Latency, from, to, household);
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
        Aggregation aggregation = Aggregation.Avg,
        string? household = null)
    {
        var events = await GetEventsAsync<VoiceEvent>(MetricKeys.Voice, from, to, household);
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
        Aggregation aggregation = Aggregation.Avg,
        string? household = null)
    {
        var events = await GetEventsAsync<SkillPreloadEvent>(MetricKeys.Skills, from, to, household);

        // By skill, an event counts once per skill it preloaded and not at all when it preloaded
        // none: the question that dimension answers is "which skills arrive early".
        var agent = AgentLabels(events, "(unknown)");
        var keyed = dimension == SkillPreloadDimension.Skill
            ? events.SelectMany(e => e.Skills.Select(skill => (Key: skill, Event: e)))
            : events.Select(e => (Key: dimension switch
            {
                SkillPreloadDimension.Outcome => e.Outcome,
                SkillPreloadDimension.Agent => agent(e),
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
                    // One judgment's tokens belong to the judgment, not to each skill it happened
                    // to preload: summing the whole event under every skill made two bars of
                    // 2,200 out of one 2,200-token call. Split across the skills it is keyed
                    // under, so the bars still total what the pass actually spent.
                    SkillPreloadMetric.InputTokens => g.Sum(k =>
                        (decimal)(k.Event.InputTokens ?? 0) / (dimension == SkillPreloadDimension.Skill
                            ? Math.Max(1, k.Event.Skills.Count)
                            : 1)),
                    _ => throw new ArgumentOutOfRangeException(nameof(metric))
                });
    }

    public async Task<Dictionary<string, decimal>> GetModalDismissalGroupedAsync(
        ModalDismissalDimension dimension,
        ModalDismissalMetric metric,
        DateOnly from,
        DateOnly to,
        Aggregation aggregation = Aggregation.Avg,
        string? household = null)
    {
        var events = await GetEventsAsync<ModalDismissalEvent>(MetricKeys.Modals, from, to, household);

        return events
            .GroupBy(e => dimension switch
            {
                ModalDismissalDimension.Kind => e.Kind,
                ModalDismissalDimension.Outcome => e.Outcome,
                _ => throw new ArgumentOutOfRangeException(nameof(dimension))
            })
            .ToDictionary(
                g => g.Key,
                g => metric switch
                {
                    ModalDismissalMetric.Count => (decimal)g.Count(),
                    // Only a judgment carries a latency; a wall a selector closed has none to average.
                    ModalDismissalMetric.LatencyMs => AggregateLatency(
                        g.Where(e => e.DurationMs is not null).Select(e => (decimal)e.DurationMs!.Value), aggregation),
                    _ => throw new ArgumentOutOfRangeException(nameof(metric))
                });
    }

    // One series per outcome, a count per bucket: the miss rate over time, and beside it the share
    // each path closed. Filtered to one kind when the page asks for it.
    public async Task<IReadOnlyList<LatencyTrendSeries>> GetModalDismissalTrendAsync(
        DateOnly from, DateOnly to, string? kind = null, string? household = null)
    {
        var events = await GetEventsAsync<ModalDismissalEvent>(MetricKeys.Modals, from, to, household);

        return CountTrend(
            events.Where(e => kind is null || e.Kind == kind),
            e => e.Outcome,
            to.DayNumber - from.DayNumber <= 2);
    }

    // One series per outcome, a count per bucket: what "the preload rate over time" is read off.
    public async Task<IReadOnlyList<LatencyTrendSeries>> GetSkillPreloadTrendAsync(DateOnly from, DateOnly to, string? household = null)
    {
        var events = await GetEventsAsync<SkillPreloadEvent>(MetricKeys.Skills, from, to, household);

        return CountTrend(events, e => e.Outcome, to.DayNumber - from.DayNumber <= 2);
    }

    // A count per bucket, zero-filled across every bucket any series has an event in. These are
    // drawn as lines, and for a count a missing bucket means none happened — left out, the line
    // runs straight from one occurrence to the next and reads as a steady rate over all the hours
    // between. A latency trend is the opposite and is deliberately not filled: there, no point
    // means nothing was measured, and a zero would claim something answered instantly.
    private static IReadOnlyList<LatencyTrendSeries> CountTrend<T>(
        IEnumerable<T> events, Func<T, string?> outcomeOf, bool hourly) where T : MetricEvent
    {
        var bucketed = events
            .Select(e => (Outcome: outcomeOf(e) ?? "(unrecorded)", Bucket: BucketTimestamp(e.Timestamp, hourly)))
            .ToList();
        var buckets = bucketed.Select(e => e.Bucket).Distinct().OrderBy(b => b).ToList();

        return bucketed
            .GroupBy(e => e.Outcome)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(outcome =>
            {
                var counts = outcome.GroupBy(e => e.Bucket).ToDictionary(b => b.Key, b => b.Count());
                return new LatencyTrendSeries(
                    outcome.Key,
                    buckets.Select(b => new LatencyTrendPoint(b, counts.GetValueOrDefault(b))).ToList());
            })
            .ToList();
    }

    public async Task<Dictionary<string, int>> GetScheduleGroupedAsync(
        ScheduleDimension dimension, DateOnly from, DateOnly to, string? household = null)
    {
        var events = await GetEventsAsync<ScheduleExecutionEvent>(MetricKeys.Schedules, from, to, household);
        return events
            .GroupBy(e => dimension switch
            {
                ScheduleDimension.Schedule => e.ScheduleId,
                ScheduleDimension.Status => e.Success ? "Success" : "Failure",
                _ => throw new ArgumentOutOfRangeException(nameof(dimension))
            })
            .ToDictionary(g => g.Key, g => g.Count());
    }

    // An agent's figures are keyed by its id, which a household's rename leaves alone, and shown under
    // the name its latest event went by (an event that carries none — one from before the name was
    // carried — shows its id); two ids that go by one name each carry their id beside it, so the
    // figures stay apart.
    // A person is keyed by their account, so a rename does not split their figures, and labelled with
    // the latest name their calls went by — never their id where any name was carried, since a memory
    // call names only the id. Two accounts going by one name each show `name (id)`. A call from before
    // accounts were carried is keyed and labelled by its name.
    private static Func<T, string> PersonLabels<T>(
        IReadOnlyCollection<T> events, Func<T, string?> userId, Func<T, string> sender) where T : MetricEvent
    {
        var named = events
            .Where(e => userId(e) is not null)
            .GroupBy(e => userId(e)!)
            .Select(g => (Id: g.Key, Name: g.Where(e => sender(e) != g.Key && !ServiceSenders.IsService(sender(e))).MaxBy(e => e.Timestamp) is { } latest
                ? sender(latest)
                : g.Key))
            .ToList();
        var shared = named.GroupBy(p => p.Name).Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet();
        var labels = named.ToDictionary(p => p.Id, p => shared.Contains(p.Name) ? $"{p.Name} ({p.Id})" : p.Name);
        return e => userId(e) is { } id ? labels[id] : sender(e);
    }

    // An account's label from the calls that named it, for events that carry only the account; null
    // for one no call named.
    private static Func<string, string?> AccountNames(IReadOnlyCollection<TokenUsageEvent> calls)
    {
        var named = calls.Where(e => e.UserId is not null).ToList();
        var label = PersonLabels(named, e => e.UserId, e => e.Sender);
        var labels = named.GroupBy(e => e.UserId!).ToDictionary(g => g.Key, g => label(g.First()));
        return id => labels.GetValueOrDefault(id);
    }

    private static Func<MetricEvent, string> AgentLabels(IEnumerable<MetricEvent> events, string unknown)
    {
        var named = events
            .Where(e => e.AgentId is not null)
            .GroupBy(e => e.AgentId!)
            .Select(g => (Id: g.Key, Name: g.Where(e => e.AgentName is not null).MaxBy(e => e.Timestamp)?.AgentName ?? g.Key))
            .ToList();
        var shared = named.GroupBy(a => a.Name).Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet();
        var labels = named.ToDictionary(a => a.Id, a => shared.Contains(a.Name) ? $"{a.Name} ({a.Id})" : a.Name);
        return e => e.AgentId is { } id ? labels[id] : unknown;
    }

    private static IEnumerable<DateOnly> EnumerateDates(DateOnly from, DateOnly to)
    {
        for (var date = from; date <= to; date = date.AddDays(1))
        {
            yield return date;
        }
    }
}