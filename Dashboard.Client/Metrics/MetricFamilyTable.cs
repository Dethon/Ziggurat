using Dashboard.Client.Services;
using Dashboard.Client.State.Errors;
using Dashboard.Client.State.Latency;
using Dashboard.Client.State.Memory;
using Dashboard.Client.State.Schedules;
using Dashboard.Client.State.Skills;
using Dashboard.Client.State.Tokens;
using Dashboard.Client.State.Tools;
using Dashboard.Client.State.Voice;
using Dashboard.Client.State.Web;
using Domain.DTOs.Metrics.Enums;

namespace Dashboard.Client.Metrics;

// The one place a metric family is declared. Nothing enforces that a new family is added here, so a
// missing one shows up as a gap in a seven-entry list rather than as a compiler error; that is the
// price recorded in docs/adr/0007-a-metric-family-is-named-not-typed.md.
public sealed class MetricFamilyTable
{
    public static readonly IReadOnlySet<MemoryMetric> UserOnlyMemoryMetrics = new HashSet<MemoryMetric>
    {
        MemoryMetric.StoredCount,
        MemoryMetric.MergedCount,
        MemoryMetric.DecayedCount,
    };

    public MetricFamilyTable(
        MetricsApiService api,
        TokensStore tokens,
        ToolsStore tools,
        ErrorsStore errors,
        SchedulesStore schedules,
        MemoryStore memory,
        LatencyStore latency,
        VoiceStore voice,
        SkillsStore skills,
        WebStore web)
    {
        Tokens = new MetricFamily<TokensStore>(
            tokens,
            "tokens",
            dimension: MetricChoice.For("groupBy", () => tokens.State.GroupBy, tokens.SetGroupBy),
            metric: MetricChoice.For("metric", () => tokens.State.Metric, tokens.SetMetric),
            setDateRange: tokens.SetDateRange,
            // Truncations are a grouped count rather than one of the family's events, but they are
            // the tokens page's fourth headline figure, so they load with them.
            // A load fetches here and hands back the store write, which MetricFamily runs only if
            // no later load has started meanwhile.
            loadEvents: async () =>
            {
                var state = tokens.State;
                var events = api.GetTokensAsync(state.From, state.To);
                var truncations = api.GetGroupedAsync<decimal>(
                    $"tokens/by/{TokenDimension.Model}", state.From, state.To,
                    [("metric", nameof(TokenMetric.TruncationCount))]);
                await Task.WhenAll(events, truncations);
                var loaded = await events ?? [];
                var truncated = (long)((await truncations)?.Values.Sum() ?? 0);
                return () =>
                {
                    tokens.SetEvents(loaded);
                    tokens.SetTruncations(truncated);
                };
            },
            refreshBreakdown: async () =>
            {
                var state = tokens.State;
                var breakdown = await api.GetGroupedAsync<decimal>(
                    $"tokens/by/{state.GroupBy}", state.From, state.To,
                    [("metric", state.Metric.ToString())]);
                tokens.SetBreakdown(breakdown ?? []);
            });

        Tools = new MetricFamily<ToolsStore>(
            tools,
            "tools",
            dimension: MetricChoice.For("groupBy", () => tools.State.GroupBy, dimension =>
            {
                tools.SetGroupBy(dimension);
                // Grouped by status there is no error rate to show, so the pill that is about to be
                // disabled cannot stay selected.
                if (dimension == ToolDimension.Status && tools.State.Metric == ToolMetric.ErrorRate)
                {
                    tools.SetMetric(ToolMetric.CallCount);
                }
            }),
            metric: MetricChoice.For("metric", () => tools.State.Metric, tools.SetMetric),
            setDateRange: tools.SetDateRange,
            loadEvents: async () =>
            {
                var state = tools.State;
                var loaded = await api.GetToolsAsync(state.From, state.To) ?? [];
                return () => tools.SetEvents(loaded);
            },
            refreshBreakdown: async () =>
            {
                var state = tools.State;
                var breakdown = await api.GetGroupedAsync<decimal>(
                    $"tools/by/{state.GroupBy}", state.From, state.To,
                    [("metric", state.Metric.ToString())]);
                tools.SetBreakdown(breakdown ?? []);
            });

        Errors = new MetricFamily<ErrorsStore>(
            errors,
            "errors",
            dimension: MetricChoice.For("groupBy", () => errors.State.GroupBy, errors.SetGroupBy),
            metric: null,
            setDateRange: errors.SetDateRange,
            loadEvents: async () =>
            {
                var state = errors.State;
                var loaded = await api.GetErrorsAsync(state.From, state.To) ?? [];
                return () => errors.SetEvents(loaded);
            },
            refreshBreakdown: async () =>
            {
                var state = errors.State;
                var breakdown = await api.GetGroupedAsync<int>(
                    $"errors/by/{state.GroupBy}", state.From, state.To);
                errors.SetBreakdown(breakdown ?? []);
            });

        Schedules = new MetricFamily<SchedulesStore>(
            schedules,
            "schedules",
            dimension: MetricChoice.For("groupBy", () => schedules.State.GroupBy, schedules.SetGroupBy),
            metric: null,
            setDateRange: schedules.SetDateRange,
            loadEvents: async () =>
            {
                var state = schedules.State;
                var loaded = await api.GetSchedulesAsync(state.From, state.To) ?? [];
                return () => schedules.SetEvents(loaded);
            },
            refreshBreakdown: async () =>
            {
                var state = schedules.State;
                var breakdown = await api.GetGroupedAsync<int>(
                    $"schedules/by/{state.GroupBy}", state.From, state.To);
                schedules.SetBreakdown(breakdown ?? []);
            });

        Memory = new MetricFamily<MemoryStore>(
            memory,
            "memory",
            dimension: MetricChoice.For("groupBy", () => memory.State.GroupBy, dimension =>
            {
                memory.SetGroupBy(dimension);
                // The stored, merged and decayed counts only exist per user, so grouping by
                // anything else cannot leave one of them selected.
                if (dimension != MemoryDimension.User && UserOnlyMemoryMetrics.Contains(memory.State.Metric))
                {
                    memory.SetMetric(MemoryMetric.Count);
                }
            }),
            metric: MetricChoice.For("metric", () => memory.State.Metric, memory.SetMetric),
            setDateRange: memory.SetDateRange,
            loadEvents: async () =>
            {
                var state = memory.State;
                var recall = api.GetMemoryRecallAsync(state.From, state.To);
                var extraction = api.GetMemoryExtractionAsync(state.From, state.To);
                var dreaming = api.GetMemoryDreamingAsync(state.From, state.To);
                var judgments = api.GetMemoryJudgmentsAsync(state.From, state.To);
                await Task.WhenAll(recall, extraction, dreaming, judgments);
                var loadedRecall = await recall ?? [];
                var loadedExtraction = await extraction ?? [];
                var loadedDreaming = await dreaming ?? [];
                var loadedJudgments = await judgments ?? [];
                return () =>
                {
                    memory.SetRecallEvents(loadedRecall);
                    memory.SetExtractionEvents(loadedExtraction);
                    memory.SetDreamingEvents(loadedDreaming);
                    memory.SetJudgmentEvents(loadedJudgments);
                };
            },
            refreshBreakdown: async () =>
            {
                var state = memory.State;
                var breakdown = await api.GetGroupedAsync<decimal>(
                    $"memory/by/{state.GroupBy}", state.From, state.To,
                    [("metric", state.Metric.ToString())]);
                memory.SetBreakdown(breakdown ?? []);
            });

        Latency = new MetricFamily<LatencyStore>(
            latency,
            "latency",
            dimension: MetricChoice.For("groupBy", () => latency.State.GroupBy, latency.SetGroupBy),
            metric: MetricChoice.For("metric", () => latency.State.Metric, latency.SetMetric),
            setDateRange: latency.SetDateRange,
            loadEvents: async () =>
            {
                var state = latency.State;
                var loaded = await api.GetLatencyAsync(state.From, state.To) ?? [];
                return () => latency.SetEvents(loaded);
            },
            // The trend is the second panel on the latency page. It is fetched alongside the
            // breakdown and written with it, so the two panels can never disagree.
            refreshBreakdown: async () =>
            {
                var state = latency.State;
                var breakdown = api.GetGroupedAsync<decimal>(
                    $"latency/by/{state.GroupBy}", state.From, state.To,
                    [("metric", state.Metric.ToString())]);
                var trend = api.GetLatencyTrendAsync(state.Metric, state.From, state.To);
                await Task.WhenAll(breakdown, trend);
                latency.SetBreakdown(await breakdown ?? []);
                latency.SetTrend(await trend ?? []);
            });

        Voice = new MetricFamily<VoiceStore>(
            voice,
            "voice",
            dimension: MetricChoice.For("groupBy", () => voice.State.GroupBy, voice.SetGroupBy),
            metric: MetricChoice.For("metric", () => voice.State.Metric, voice.SetMetric),
            setDateRange: voice.SetDateRange,
            loadEvents: async () =>
            {
                var state = voice.State;
                var loaded = await api.GetVoiceEventsAsync(state.From, state.To) ?? [];
                return () => voice.SetEvents(loaded);
            },
            refreshBreakdown: async () =>
            {
                var state = voice.State;
                var breakdown = await api.GetGroupedAsync<decimal>(
                    $"voice/by/{state.GroupBy}", state.From, state.To,
                    [("metric", state.Metric.ToString()), ("agg", state.Agg.ToString())]);
                voice.SetBreakdown(breakdown ?? []);
            });

        Skills = new MetricFamily<SkillsStore>(
            skills,
            "skills",
            dimension: MetricChoice.For("groupBy", () => skills.State.GroupBy, skills.SetGroupBy),
            metric: MetricChoice.For("metric", () => skills.State.Metric, skills.SetMetric),
            setDateRange: skills.SetDateRange,
            loadEvents: async () =>
            {
                var state = skills.State;
                var loaded = await api.GetSkillPreloadEventsAsync(state.From, state.To) ?? [];
                return () => skills.SetEvents(loaded);
            },
            // The outcome trend rides the breakdown refresh, as Web's does: that is the read a
            // pill moves and a push brings back into line. Loading it with the events alone left
            // the page's headline chart frozen while preloads arrived under it.
            refreshBreakdown: async () =>
            {
                var state = skills.State;
                var breakdown = api.GetGroupedAsync<decimal>(
                    $"skills/by/{state.GroupBy}", state.From, state.To,
                    [("metric", state.Metric.ToString()), ("agg", state.Agg.ToString())]);
                var trend = api.GetSkillPreloadTrendAsync(state.From, state.To);
                await Task.WhenAll(breakdown, trend);
                skills.SetBreakdown(await breakdown ?? []);
                skills.SetTrend(await trend ?? []);
            });

        Web = new MetricFamily<WebStore>(
            web,
            "web",
            dimension: MetricChoice.For("groupBy", () => web.State.GroupBy, web.SetGroupBy),
            metric: MetricChoice.For("metric", () => web.State.Metric, web.SetMetric),
            setDateRange: web.SetDateRange,
            loadEvents: async () =>
            {
                var state = web.State;
                var loaded = await api.GetModalDismissalEventsAsync(state.From, state.To) ?? [];
                return () => web.SetEvents(loaded);
            },
            // The trend is drawn for the kind the page chose, so it rides the breakdown refresh —
            // the read a pill moves and a push brings back into line — rather than the event load.
            refreshBreakdown: async () =>
            {
                var state = web.State;
                var kind = state.Kind == WebState.AllKinds ? null : state.Kind;
                var breakdown = api.GetGroupedAsync<decimal>(
                    $"modals/by/{state.GroupBy}", state.From, state.To,
                    [("metric", state.Metric.ToString()), ("agg", state.Agg.ToString())]);
                var trend = api.GetModalDismissalTrendAsync(state.From, state.To, kind);
                await Task.WhenAll(breakdown, trend);
                web.SetBreakdown(await breakdown ?? []);
                web.SetTrend(await trend ?? []);
            });

        All = [Tokens, Tools, Errors, Schedules, Memory, Latency, Voice, Skills, Web];
        OverviewFamilies = [Tokens, Tools, Errors, Schedules, Voice];
    }

    public MetricFamily<TokensStore> Tokens { get; }
    public MetricFamily<ToolsStore> Tools { get; }
    public MetricFamily<ErrorsStore> Errors { get; }
    public MetricFamily<SchedulesStore> Schedules { get; }
    public MetricFamily<SkillsStore> Skills { get; }
    public MetricFamily<WebStore> Web { get; }
    public MetricFamily<MemoryStore> Memory { get; }
    public MetricFamily<LatencyStore> Latency { get; }
    public MetricFamily<VoiceStore> Voice { get; }

    public IReadOnlyList<MetricFamily> All { get; }

    // The families the Overview page draws, and therefore the ones its own time pill stamps: the
    // four behind the activity feed plus voice behind its two KPI cards. Every other page draws one
    // family and stamps that one.
    public IReadOnlyList<MetricFamily> OverviewFamilies { get; }
}