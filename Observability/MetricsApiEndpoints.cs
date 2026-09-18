using Domain.DTOs.Metrics;
using Domain.DTOs.Metrics.Enums;
using Observability.Services;

namespace Observability;

public static class MetricsApiEndpoints
{
    public static void MapMetricsApi(this WebApplication app)
    {
        var api = app.MapGroup("/api/metrics");

        api.MapGet("/summary", async (MetricsQueryService query, MetricDateRange range) =>
            await query.GetSummaryAsync(range.From, range.To));

        api.MapGet("/tokens", async (MetricsQueryService query, MetricDateRange range) =>
            await query.GetEventsAsync<TokenUsageEvent>("metrics:tokens:", range.From, range.To));

        api.MapGet("/tools", async (MetricsQueryService query, MetricDateRange range) =>
            await query.GetEventsAsync<ToolCallEvent>("metrics:tools:", range.From, range.To));

        api.MapGet("/errors", async (MetricsQueryService query, int? limit) =>
            await query.GetRecentErrorsAsync(limit ?? 100));

        api.MapGet("/errors/range", async (MetricsQueryService query, MetricDateRange range) =>
            await query.GetEventsAsync<ErrorEvent>("metrics:errors:", range.From, range.To));

        api.MapGet("/schedules", async (MetricsQueryService query, MetricDateRange range) =>
            await query.GetEventsAsync<ScheduleExecutionEvent>("metrics:schedules:", range.From, range.To));

        api.MapGet("/health", async (MetricsQueryService query) =>
            await query.GetHealthAsync());

        api.MapGet("/tokens/by-user", async (MetricsQueryService query, MetricDateRange range) =>
            await query.GetTokenBreakdownAsync("tokens:byUser:", range.From, range.To));

        api.MapGet("/tokens/by-model", async (MetricsQueryService query, MetricDateRange range) =>
            await query.GetTokenBreakdownAsync("tokens:byModel:", range.From, range.To));

        api.MapGet("/tokens/by/{dimension}", async (
            MetricsQueryService query,
            TokenDimension dimension,
            TokenMetric metric,
            MetricDateRange range) =>
            await query.GetTokenGroupedAsync(dimension, metric, range.From, range.To));

        api.MapGet("/tools/by/{dimension}", async (
            MetricsQueryService query,
            ToolDimension dimension,
            ToolMetric metric,
            MetricDateRange range) =>
            await query.GetToolGroupedAsync(dimension, metric, range.From, range.To));

        api.MapGet("/errors/by/{dimension}", async (
            MetricsQueryService query,
            ErrorDimension dimension,
            MetricDateRange range) =>
            await query.GetErrorGroupedAsync(dimension, range.From, range.To));

        api.MapGet("/schedules/by/{dimension}", async (
            MetricsQueryService query,
            ScheduleDimension dimension,
            MetricDateRange range) =>
            await query.GetScheduleGroupedAsync(dimension, range.From, range.To));

        api.MapGet("/memory/recall", async (MetricsQueryService query, MetricDateRange range) =>
            await query.GetEventsAsync<MemoryRecallEvent>("metrics:memory-recall:", range.From, range.To));

        api.MapGet("/memory/extraction", async (MetricsQueryService query, MetricDateRange range) =>
            await query.GetEventsAsync<MemoryExtractionEvent>("metrics:memory-extraction:", range.From, range.To));

        api.MapGet("/memory/dreaming", async (MetricsQueryService query, MetricDateRange range) =>
            await query.GetEventsAsync<MemoryDreamingEvent>("metrics:memory-dreaming:", range.From, range.To));

        api.MapGet("/memory/judgments", async (MetricsQueryService query, MetricDateRange range) =>
            await query.GetEventsAsync<MemoryJudgmentEvent>("metrics:memory-judgment:", range.From, range.To));

        api.MapGet("/memory/by/{dimension}", async (
            MetricsQueryService query,
            MemoryDimension dimension,
            MemoryMetric metric,
            MetricDateRange range) =>
            await query.GetMemoryGroupedAsync(dimension, metric, range.From, range.To));

        api.MapGet("/latency", async (MetricsQueryService query, MetricDateRange range) =>
            await query.GetEventsAsync<LatencyEvent>("metrics:latency:", range.From, range.To));

        api.MapGet("/latency/by/{dimension}", async (
            MetricsQueryService query,
            LatencyDimension dimension,
            Aggregation metric,
            MetricDateRange range) =>
            await query.GetLatencyGroupedAsync(dimension, metric, range.From, range.To));

        api.MapGet("/latency/trend", async (
            MetricsQueryService query,
            Aggregation metric,
            MetricDateRange range) =>
            await query.GetLatencyTrendAsync(metric, range.From, range.To));

        api.MapGet("/voice", async (MetricsQueryService query, MetricDateRange range) =>
            await query.GetEventsAsync<VoiceEvent>("metrics:voice:", range.From, range.To));

        api.MapGet("/voice/by/{dimension}", async (
            MetricsQueryService query,
            VoiceDimension dimension,
            VoiceMetric metric,
            Aggregation? agg,
            MetricDateRange range) =>
            await query.GetVoiceGroupedAsync(dimension, metric, range.From, range.To, agg ?? Aggregation.Avg));

        api.MapGet("/skills", async (MetricsQueryService query, MetricDateRange range) =>
            await query.GetEventsAsync<SkillPreloadEvent>("metrics:skills:", range.From, range.To));

        api.MapGet("/skills/by/{dimension}", async (
            MetricsQueryService query,
            SkillPreloadDimension dimension,
            SkillPreloadMetric metric,
            Aggregation? agg,
            MetricDateRange range) =>
            await query.GetSkillPreloadGroupedAsync(dimension, metric, range.From, range.To, agg ?? Aggregation.Avg));

        api.MapGet("/skills/trend", async (MetricsQueryService query, MetricDateRange range) =>
            await query.GetSkillPreloadTrendAsync(range.From, range.To));

        api.MapGet("/modals", async (MetricsQueryService query, MetricDateRange range) =>
            await query.GetEventsAsync<ModalDismissalEvent>("metrics:modals:", range.From, range.To));

        api.MapGet("/modals/by/{dimension}", async (
            MetricsQueryService query,
            ModalDismissalDimension dimension,
            ModalDismissalMetric metric,
            Aggregation? agg,
            MetricDateRange range) =>
            await query.GetModalDismissalGroupedAsync(dimension, metric, range.From, range.To, agg ?? Aggregation.Avg));

        api.MapGet("/modals/trend", async (MetricsQueryService query, MetricDateRange range, string? kind) =>
            await query.GetModalDismissalTrendAsync(range.From, range.To, kind));
    }
}