using System.Text.Json;
using Domain.DTOs.Metrics;
using Domain.DTOs.Metrics.Enums;
using Domain.Metrics;
using Microsoft.AspNetCore.SignalR;
using Observability.Hubs;
using StackExchange.Redis;

namespace Observability.Services;

// Household is null for the deployment's own services and a probed container.
public record ServiceHealthUpdate(string Service, bool IsHealthy, DateTimeOffset Timestamp, string? Household = null);

// The totals-hash fields Lemonade's use is counted under.
public static class LemonadeTotals
{
    public const string TranscribedMs = "lemonade:transcription:ms";
    public const string SpokenCharacters = "lemonade:speech:chars";
    public const string Embeddings = "lemonade:embedding:count";
}

public sealed class MetricsCollectorService(
    IConnectionMultiplexer redis,
    IHubContext<MetricsHub> hubContext,
    ILogger<MetricsCollectorService> logger,
    TimeProvider? timeProvider = null) : BackgroundService
{
    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private static readonly TimeSpan _dailyKeyTtl = TimeSpan.FromDays(30);
    private static readonly TimeSpan _healthCheckInterval = TimeSpan.FromSeconds(15);

    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await DropLegacyRosterAsync(redis.GetDatabase());

        var subscriber = redis.GetSubscriber();

        await subscriber.SubscribeAsync(
            RedisChannel.Literal("metrics:events"),
            async void (_, message) =>
            {
                try
                {
                    var evt = JsonSerializer.Deserialize<MetricEvent>((string)message!, _jsonOptions);
                    if (evt is null)
                    {
                        return;
                    }

                    var db = redis.GetDatabase();
                    await ProcessEventAsync(evt, db);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Failed to process metric event");
                }
            });

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await Task.Delay(_healthCheckInterval, stoppingToken);
                await CheckHealthAsync();
            }
        }
        catch (OperationCanceledException)
        {
            // Graceful shutdown
        }

        await subscriber.UnsubscribeAsync(RedisChannel.Literal("metrics:events"));
    }

    // The predecessor roster (a plain set) was only ever added to, so a service that was retired
    // outright kept a permanently-offline tile. Dropped once at startup; the sorted-set roster in
    // ServiceHealthRegistry replaces it and ages its own entries out.
    internal Task DropLegacyRosterAsync(IDatabase db) =>
        db.KeyDeleteAsync(ServiceHealthRegistry.LegacyKey);

    internal async Task CheckHealthAsync()
    {
        try
        {
            var db = redis.GetDatabase();
            var now = _time.GetUtcNow();
            var roster = await ServiceHealthRegistry.ListAsync(db, now);

            foreach (var member in roster)
            {
                var isHealthy = await db.KeyExistsAsync(ServiceHealthRegistry.HealthKey(member));
                if (!isHealthy)
                {
                    var (service, tenantId) = ServiceHealthRegistry.Parse(member);
                    await hubContext.Clients.All.SendAsync("OnHealthUpdate",
                        new ServiceHealthUpdate(service, false, now, tenantId));
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to check service health");
        }
    }

    internal async Task ProcessEventAsync(MetricEvent evt, IDatabase db)
    {
        await db.SetAddAsync(MetricKeys.Scopes, MetricKeys.ScopeOf(evt.TenantId));

        switch (evt)
        {
            case TokenUsageEvent token:
                await ProcessTokenUsageAsync(token, db);
                break;
            case ToolCallEvent tool:
                await ProcessToolCallAsync(tool, db);
                break;
            case ErrorEvent error:
                await ProcessErrorAsync(error, db);
                break;
            case ScheduleExecutionEvent schedule:
                await ProcessScheduleExecutionAsync(schedule, db);
                break;
            case HeartbeatEvent heartbeat:
                await ProcessHeartbeatAsync(heartbeat, db);
                break;
            case MemoryRecallEvent recall:
                await ProcessMemoryRecallAsync(recall, db);
                break;
            case MemoryExtractionEvent extraction:
                await ProcessMemoryExtractionAsync(extraction, db);
                break;
            case MemoryDreamingEvent dreaming:
                await ProcessMemoryDreamingAsync(dreaming, db);
                break;
            case ContextTruncationEvent truncation:
                await ProcessContextTruncationAsync(truncation, db);
                break;
            case LatencyEvent latency:
                await ProcessLatencyAsync(latency, db);
                break;
            case VoiceEvent voice:
                await ProcessVoiceAsync(voice, db);
                break;
            case OutpostEvent outpost:
                await ProcessOutpostAsync(outpost, db);
                break;
            case SkillPreloadEvent preload:
                await ProcessSkillPreloadAsync(preload, db);
                break;
            case MemoryJudgmentEvent judgment:
                await ProcessMemoryJudgmentAsync(judgment, db);
                break;
            case ModalDismissalEvent modal:
                await ProcessModalDismissalAsync(modal, db);
                break;
            case ExecScreenEvent screen:
                await ProcessExecScreenAsync(screen, db);
                break;
            case LemonadeUsageEvent lemonade:
                await ProcessLemonadeUsageAsync(lemonade, db);
                break;
        }
    }

    private async Task ProcessTokenUsageAsync(TokenUsageEvent evt, IDatabase db)
    {
        var scope = MetricKeys.ScopeOf(evt.TenantId);
        var sortedSetKey = MetricKeys.Daily(MetricKeys.Tokens, scope, evt.Timestamp);
        var totalsKey = MetricKeys.Daily(MetricKeys.Totals, scope, evt.Timestamp);
        var score = evt.Timestamp.ToUnixTimeMilliseconds();
        var json = JsonSerializer.Serialize<MetricEvent>(evt, _jsonOptions);

        await Task.WhenAll(
            db.SortedSetAddAsync(sortedSetKey, json, score),
            db.HashIncrementAsync(totalsKey, "tokens:input", evt.InputTokens),
            db.HashIncrementAsync(totalsKey, "tokens:output", evt.OutputTokens),
            db.HashIncrementAsync(totalsKey, "tokens:cost", (long)(evt.Cost * 10000m)),
            db.HashIncrementAsync(totalsKey, $"tokens:byUser:{evt.UserId ?? evt.Sender}", evt.InputTokens + evt.OutputTokens),
            db.HashIncrementAsync(totalsKey, $"tokens:byModel:{evt.Model}", evt.InputTokens + evt.OutputTokens),
            db.KeyExpireAsync(sortedSetKey, _dailyKeyTtl, ExpireWhen.HasNoExpiry),
            db.KeyExpireAsync(totalsKey, _dailyKeyTtl, ExpireWhen.HasNoExpiry));

        await hubContext.Clients.All.SendAsync("OnTokenUsage", evt);
    }

    private async Task ProcessToolCallAsync(ToolCallEvent evt, IDatabase db)
    {
        var scope = MetricKeys.ScopeOf(evt.TenantId);
        var sortedSetKey = MetricKeys.Daily(MetricKeys.Tools, scope, evt.Timestamp);
        var totalsKey = MetricKeys.Daily(MetricKeys.Totals, scope, evt.Timestamp);
        var score = evt.Timestamp.ToUnixTimeMilliseconds();
        var json = JsonSerializer.Serialize<MetricEvent>(evt, _jsonOptions);

        var tasks = new List<Task>
        {
            db.SortedSetAddAsync(sortedSetKey, json, score),
            db.HashIncrementAsync(totalsKey, "tools:count"),
            db.HashIncrementAsync(totalsKey, $"tools:byName:{evt.ToolName}"),
            db.KeyExpireAsync(sortedSetKey, _dailyKeyTtl, ExpireWhen.HasNoExpiry),
            db.KeyExpireAsync(totalsKey, _dailyKeyTtl, ExpireWhen.HasNoExpiry)
        };

        if (!evt.Success)
        {
            tasks.Add(db.HashIncrementAsync(totalsKey, "tools:errors"));
        }

        await Task.WhenAll(tasks);

        await hubContext.Clients.All.SendAsync("OnToolCall", evt);

        if (!evt.Success)
        {
            var errorEvent = new ErrorEvent
            {
                Service = "ToolCall",
                ErrorType = evt.ToolName,
                Message = evt.Error ?? "Tool call failed",
                Timestamp = evt.Timestamp,
                AgentId = evt.AgentId,
                ConversationId = evt.ConversationId,
                TenantId = evt.TenantId
            };
            await ProcessErrorAsync(errorEvent, db);
        }
    }

    private async Task ProcessErrorAsync(ErrorEvent evt, IDatabase db)
    {
        var scope = MetricKeys.ScopeOf(evt.TenantId);
        var sortedSetKey = MetricKeys.Daily(MetricKeys.Errors, scope, evt.Timestamp);
        var recentKey = MetricKeys.RecentErrors(scope);
        var score = evt.Timestamp.ToUnixTimeMilliseconds();
        var json = JsonSerializer.Serialize<MetricEvent>(evt, _jsonOptions);

        await Task.WhenAll(
            db.SortedSetAddAsync(sortedSetKey, json, score),
            db.ListLeftPushAsync(recentKey, json),
            db.KeyExpireAsync(sortedSetKey, _dailyKeyTtl, ExpireWhen.HasNoExpiry));

        await db.ListTrimAsync(recentKey, 0, 99);

        await hubContext.Clients.All.SendAsync("OnError", evt);
    }

    private async Task ProcessScheduleExecutionAsync(ScheduleExecutionEvent evt, IDatabase db)
    {
        var scope = MetricKeys.ScopeOf(evt.TenantId);
        var sortedSetKey = MetricKeys.Daily(MetricKeys.Schedules, scope, evt.Timestamp);
        var score = evt.Timestamp.ToUnixTimeMilliseconds();
        var json = JsonSerializer.Serialize<MetricEvent>(evt, _jsonOptions);

        await Task.WhenAll(
            db.SortedSetAddAsync(sortedSetKey, json, score),
            db.KeyExpireAsync(sortedSetKey, _dailyKeyTtl, ExpireWhen.HasNoExpiry));

        await hubContext.Clients.All.SendAsync("OnScheduleExecution", evt);
    }

    // Stored as a day-keyed time series like a schedule execution, and pushed nowhere: outposts
    // have no view in the dashboard on purpose. What this answers is "was that machine up at two
    // o'clock", which nothing else can answer — an outpost leaves no other trace of having been
    // there.
    private async Task ProcessOutpostAsync(OutpostEvent evt, IDatabase db)
    {
        var scope = MetricKeys.ScopeOf(evt.TenantId);
        var sortedSetKey = MetricKeys.Daily(MetricKeys.Outposts, scope, evt.Timestamp);
        var json = JsonSerializer.Serialize<MetricEvent>(evt, _jsonOptions);

        await Task.WhenAll(
            db.SortedSetAddAsync(sortedSetKey, json, evt.Timestamp.ToUnixTimeMilliseconds()),
            db.KeyExpireAsync(sortedSetKey, _dailyKeyTtl, ExpireWhen.HasNoExpiry));
    }

    private async Task ProcessHeartbeatAsync(HeartbeatEvent evt, IDatabase db)
    {
        // Keyed by household too: two households' servers of one service are two tiles, not one
        // that whichever beat last paints.
        var member = ServiceHealthRegistry.Member(evt.Service, evt.TenantId);
        var json = JsonSerializer.Serialize<MetricEvent>(evt, _jsonOptions);

        await Task.WhenAll(
            db.StringSetAsync(ServiceHealthRegistry.HealthKey(member), json, TimeSpan.FromSeconds(60)),
            ServiceHealthRegistry.MarkSeenAsync(db, member, evt.Timestamp));

        await hubContext.Clients.All.SendAsync("OnHealthUpdate",
            new ServiceHealthUpdate(evt.Service, true, evt.Timestamp, evt.TenantId));
    }

    // Kept as each household's running totals in their own units — milliseconds of speech heard,
    // characters spoken, texts embedded — and pushed nowhere: the household view is the admin
    // area's, and the events stay for re-reading by channel.
    private async Task ProcessLemonadeUsageAsync(LemonadeUsageEvent evt, IDatabase db)
    {
        var scope = MetricKeys.ScopeOf(evt.TenantId);
        var sortedSetKey = MetricKeys.Daily(MetricKeys.Lemonade, scope, evt.Timestamp);
        var totalsKey = MetricKeys.Daily(MetricKeys.Totals, scope, evt.Timestamp);
        var json = JsonSerializer.Serialize<MetricEvent>(evt, _jsonOptions);
        var (field, amount) = evt.Use switch
        {
            LemonadeUse.Transcription => (LemonadeTotals.TranscribedMs, (long)Math.Round(evt.Amount * 1000)),
            LemonadeUse.Speech => (LemonadeTotals.SpokenCharacters, (long)evt.Amount),
            _ => (LemonadeTotals.Embeddings, (long)evt.Amount)
        };

        await Task.WhenAll(
            db.SortedSetAddAsync(sortedSetKey, json, evt.Timestamp.ToUnixTimeMilliseconds()),
            db.HashIncrementAsync(totalsKey, field, amount),
            db.KeyExpireAsync(sortedSetKey, _dailyKeyTtl, ExpireWhen.HasNoExpiry),
            db.KeyExpireAsync(totalsKey, _dailyKeyTtl, ExpireWhen.HasNoExpiry));
    }

    private async Task ProcessLatencyAsync(LatencyEvent evt, IDatabase db)
    {
        var scope = MetricKeys.ScopeOf(evt.TenantId);
        var sortedSetKey = MetricKeys.Daily(MetricKeys.Latency, scope, evt.Timestamp);
        var totalsKey = MetricKeys.Daily(MetricKeys.Totals, scope, evt.Timestamp);
        var score = evt.Timestamp.ToUnixTimeMilliseconds();
        var json = JsonSerializer.Serialize<MetricEvent>(evt, _jsonOptions);

        await Task.WhenAll(
            db.SortedSetAddAsync(sortedSetKey, json, score),
            db.HashIncrementAsync(totalsKey, $"latency:{evt.Stage}:count"),
            db.HashIncrementAsync(totalsKey, $"latency:{evt.Stage}:totalMs", evt.DurationMs),
            db.KeyExpireAsync(sortedSetKey, _dailyKeyTtl, ExpireWhen.HasNoExpiry),
            db.KeyExpireAsync(totalsKey, _dailyKeyTtl, ExpireWhen.HasNoExpiry));

        await hubContext.Clients.All.SendAsync("OnLatency", evt);
    }

    private async Task ProcessMemoryRecallAsync(MemoryRecallEvent evt, IDatabase db)
    {
        var scope = MetricKeys.ScopeOf(evt.TenantId);
        var sortedSetKey = MetricKeys.Daily(MetricKeys.MemoryRecall, scope, evt.Timestamp);
        var totalsKey = MetricKeys.Daily(MetricKeys.Totals, scope, evt.Timestamp);
        var score = evt.Timestamp.ToUnixTimeMilliseconds();
        var json = JsonSerializer.Serialize<MetricEvent>(evt, _jsonOptions);

        await Task.WhenAll(
            db.SortedSetAddAsync(sortedSetKey, json, score),
            db.HashIncrementAsync(totalsKey, "memory:recalls"),
            db.HashIncrementAsync(totalsKey, "memory:recallDuration", evt.DurationMs),
            db.HashIncrementAsync(totalsKey, "memory:recallMemories", evt.MemoryCount),
            db.HashIncrementAsync(totalsKey, $"memory:byUser:{evt.UserId}"),
            db.KeyExpireAsync(sortedSetKey, _dailyKeyTtl, ExpireWhen.HasNoExpiry),
            db.KeyExpireAsync(totalsKey, _dailyKeyTtl, ExpireWhen.HasNoExpiry));

        await hubContext.Clients.All.SendAsync("OnMemoryRecall", evt);
    }

    private async Task ProcessMemoryExtractionAsync(MemoryExtractionEvent evt, IDatabase db)
    {
        var scope = MetricKeys.ScopeOf(evt.TenantId);
        var sortedSetKey = MetricKeys.Daily(MetricKeys.MemoryExtraction, scope, evt.Timestamp);
        var totalsKey = MetricKeys.Daily(MetricKeys.Totals, scope, evt.Timestamp);
        var score = evt.Timestamp.ToUnixTimeMilliseconds();
        var json = JsonSerializer.Serialize<MetricEvent>(evt, _jsonOptions);

        await Task.WhenAll(
            db.SortedSetAddAsync(sortedSetKey, json, score),
            db.HashIncrementAsync(totalsKey, "memory:extractions"),
            db.HashIncrementAsync(totalsKey, "memory:extractionDuration", evt.DurationMs),
            db.HashIncrementAsync(totalsKey, "memory:candidates", evt.CandidateCount),
            db.HashIncrementAsync(totalsKey, "memory:stored", evt.StoredCount),
            db.HashIncrementAsync(totalsKey, $"memory:outcome:{evt.Outcome ?? "unrecorded"}"),
            db.HashIncrementAsync(totalsKey, $"memory:byUser:{evt.UserId}"),
            db.KeyExpireAsync(sortedSetKey, _dailyKeyTtl, ExpireWhen.HasNoExpiry),
            db.KeyExpireAsync(totalsKey, _dailyKeyTtl, ExpireWhen.HasNoExpiry));

        await hubContext.Clients.All.SendAsync("OnMemoryExtraction", evt);
    }

    private async Task ProcessMemoryJudgmentAsync(MemoryJudgmentEvent evt, IDatabase db)
    {
        var scope = MetricKeys.ScopeOf(evt.TenantId);
        var sortedSetKey = MetricKeys.Daily(MetricKeys.MemoryJudgment, scope, evt.Timestamp);
        var totalsKey = MetricKeys.Daily(MetricKeys.Totals, scope, evt.Timestamp);
        var json = JsonSerializer.Serialize<MetricEvent>(evt, _jsonOptions);

        var tasks = new List<Task>
        {
            db.SortedSetAddAsync(sortedSetKey, json, evt.Timestamp.ToUnixTimeMilliseconds()),
            db.HashIncrementAsync(totalsKey, $"memory:judgments:{evt.Kind}"),
            db.KeyExpireAsync(sortedSetKey, _dailyKeyTtl, ExpireWhen.HasNoExpiry),
            db.KeyExpireAsync(totalsKey, _dailyKeyTtl, ExpireWhen.HasNoExpiry)
        };

        if (evt.Dropped == true)
        {
            tasks.Add(db.HashIncrementAsync(totalsKey, "memory:drops"));
        }

        await Task.WhenAll(tasks);

        await hubContext.Clients.All.SendAsync("OnMemoryJudgment", evt);
    }

    private async Task ProcessMemoryDreamingAsync(MemoryDreamingEvent evt, IDatabase db)
    {
        var scope = MetricKeys.ScopeOf(evt.TenantId);
        var sortedSetKey = MetricKeys.Daily(MetricKeys.MemoryDreaming, scope, evt.Timestamp);
        var totalsKey = MetricKeys.Daily(MetricKeys.Totals, scope, evt.Timestamp);
        var score = evt.Timestamp.ToUnixTimeMilliseconds();
        var json = JsonSerializer.Serialize<MetricEvent>(evt, _jsonOptions);

        var tasks = new List<Task>
        {
            db.SortedSetAddAsync(sortedSetKey, json, score),
            db.HashIncrementAsync(totalsKey, "memory:dreamings"),
            db.HashIncrementAsync(totalsKey, "memory:merged", evt.MergedCount),
            db.HashIncrementAsync(totalsKey, "memory:decayed", evt.DecayedCount),
            db.KeyExpireAsync(sortedSetKey, _dailyKeyTtl, ExpireWhen.HasNoExpiry),
            db.KeyExpireAsync(totalsKey, _dailyKeyTtl, ExpireWhen.HasNoExpiry)
        };

        if (evt.ProfileRegenerated)
        {
            tasks.Add(db.HashIncrementAsync(totalsKey, "memory:profileRegens"));
        }

        await Task.WhenAll(tasks);

        await hubContext.Clients.All.SendAsync("OnMemoryDreaming", evt);
    }

    private async Task ProcessContextTruncationAsync(ContextTruncationEvent evt, IDatabase db)
    {
        var scope = MetricKeys.ScopeOf(evt.TenantId);
        var sortedSetKey = MetricKeys.Daily(MetricKeys.Truncations, scope, evt.Timestamp);
        var score = evt.Timestamp.ToUnixTimeMilliseconds();
        var json = JsonSerializer.Serialize<MetricEvent>(evt, _jsonOptions);

        await Task.WhenAll(
            db.SortedSetAddAsync(sortedSetKey, json, score),
            db.KeyExpireAsync(sortedSetKey, _dailyKeyTtl, ExpireWhen.HasNoExpiry));

        await hubContext.Clients.All.SendAsync("OnContextTruncation", evt);
    }

    // Counted per outcome, so the totals hash answers "how many walls were left standing today"
    // beside "how many a judgment closed"; the latency is the judge's and accumulates as the
    // preload's does.
    private async Task ProcessModalDismissalAsync(ModalDismissalEvent evt, IDatabase db)
    {
        var scope = MetricKeys.ScopeOf(evt.TenantId);
        var sortedSetKey = MetricKeys.Daily(MetricKeys.Modals, scope, evt.Timestamp);
        var totalsKey = MetricKeys.Daily(MetricKeys.Totals, scope, evt.Timestamp);
        var json = JsonSerializer.Serialize<MetricEvent>(evt, _jsonOptions);

        var tasks = new List<Task>
        {
            db.SortedSetAddAsync(sortedSetKey, json, evt.Timestamp.ToUnixTimeMilliseconds()),
            db.HashIncrementAsync(totalsKey, $"modals:{evt.Outcome}:count"),
            db.KeyExpireAsync(sortedSetKey, _dailyKeyTtl, ExpireWhen.HasNoExpiry),
            db.KeyExpireAsync(totalsKey, _dailyKeyTtl, ExpireWhen.HasNoExpiry)
        };

        if (evt.DurationMs is { } durationMs)
        {
            tasks.Add(db.HashIncrementAsync(totalsKey, "modals:latency:count"));
            tasks.Add(db.HashIncrementAsync(totalsKey, "modals:latency:totalMs", durationMs));
        }

        await Task.WhenAll(tasks);

        await hubContext.Clients.All.SendAsync("OnModalDismissal", evt);
    }

    // Counted per outcome, so the totals hash answers "how many turns got a head start today"
    // beside "how many timed out"; the latency accumulates as voice's does.
    private async Task ProcessSkillPreloadAsync(SkillPreloadEvent evt, IDatabase db)
    {
        var scope = MetricKeys.ScopeOf(evt.TenantId);
        var sortedSetKey = MetricKeys.Daily(MetricKeys.Skills, scope, evt.Timestamp);
        var totalsKey = MetricKeys.Daily(MetricKeys.Totals, scope, evt.Timestamp);
        var json = JsonSerializer.Serialize<MetricEvent>(evt, _jsonOptions);

        var tasks = new List<Task>
        {
            db.SortedSetAddAsync(sortedSetKey, json, evt.Timestamp.ToUnixTimeMilliseconds()),
            db.HashIncrementAsync(totalsKey, $"skills:{evt.Outcome}:count"),
            db.KeyExpireAsync(sortedSetKey, _dailyKeyTtl, ExpireWhen.HasNoExpiry),
            db.KeyExpireAsync(totalsKey, _dailyKeyTtl, ExpireWhen.HasNoExpiry)
        };

        if (evt.DurationMs is { } durationMs)
        {
            tasks.Add(db.HashIncrementAsync(totalsKey, "skills:latency:count"));
            tasks.Add(db.HashIncrementAsync(totalsKey, "skills:latency:totalMs", durationMs));
        }

        await Task.WhenAll(tasks);

        await hubContext.Clients.All.SendAsync("OnSkillPreload", evt);
    }

    // Counted per reach and outcome, so the totals hash answers "how many sandbox execs were asked
    // about today" — the screen's false-positive rate — beside how many outpost execs were. No
    // dashboard page reads these yet; the events are kept for re-reading the bars.
    private async Task ProcessExecScreenAsync(ExecScreenEvent evt, IDatabase db)
    {
        var scope = MetricKeys.ScopeOf(evt.TenantId);
        var sortedSetKey = MetricKeys.Daily(MetricKeys.ExecScreen, scope, evt.Timestamp);
        var totalsKey = MetricKeys.Daily(MetricKeys.Totals, scope, evt.Timestamp);
        var json = JsonSerializer.Serialize<MetricEvent>(evt, _jsonOptions);

        var tasks = new List<Task>
        {
            db.SortedSetAddAsync(sortedSetKey, json, evt.Timestamp.ToUnixTimeMilliseconds()),
            db.HashIncrementAsync(totalsKey, $"execscreen:{evt.Reach}:{evt.Outcome}:count"),
            db.KeyExpireAsync(sortedSetKey, _dailyKeyTtl, ExpireWhen.HasNoExpiry),
            db.KeyExpireAsync(totalsKey, _dailyKeyTtl, ExpireWhen.HasNoExpiry)
        };

        if (evt.DurationMs is { } durationMs)
        {
            tasks.Add(db.HashIncrementAsync(totalsKey, "execscreen:latency:count"));
            tasks.Add(db.HashIncrementAsync(totalsKey, "execscreen:latency:totalMs", durationMs));
        }

        await Task.WhenAll(tasks);
    }

    private async Task ProcessVoiceAsync(VoiceEvent evt, IDatabase db)
    {
        var scope = MetricKeys.ScopeOf(evt.TenantId);
        var sortedSetKey = MetricKeys.Daily(MetricKeys.Voice, scope, evt.Timestamp);
        var totalsKey = MetricKeys.Daily(MetricKeys.Totals, scope, evt.Timestamp);
        var score = evt.Timestamp.ToUnixTimeMilliseconds();
        var json = JsonSerializer.Serialize<MetricEvent>(evt, _jsonOptions);

        var tasks = new List<Task>
        {
            db.SortedSetAddAsync(sortedSetKey, json, score),
            db.HashIncrementAsync(totalsKey, $"voice:{evt.Metric}:count"),
            db.KeyExpireAsync(sortedSetKey, _dailyKeyTtl, ExpireWhen.HasNoExpiry),
            db.KeyExpireAsync(totalsKey, _dailyKeyTtl, ExpireWhen.HasNoExpiry)
        };

        // Accumulate duration for latency-type metrics so summaries can report average latency,
        // matching how LatencyEvent stores both count and totalMs.
        if (evt.DurationMs is { } durationMs)
        {
            tasks.Add(db.HashIncrementAsync(totalsKey, $"voice:{evt.Metric}:totalMs", durationMs));
        }

        await Task.WhenAll(tasks);

        await hubContext.Clients.All.SendAsync("OnVoice", evt);
    }
}