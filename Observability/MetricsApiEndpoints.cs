using Domain.DTOs.Metrics;
using Domain.DTOs.Metrics.Enums;
using Domain.Metrics;
using Observability.Services;

namespace Observability;

// Every read takes an optional ?household=: a household's id, "deployment" for the deployment's own
// events, or absent for everybody's (MetricsQueryService).
public static class MetricsApiEndpoints
{
    public static void MapMetricsApi(this WebApplication app)
    {
        var api = app.MapGroup("/api/metrics");

        api.MapGet("/summary", async (MetricsQueryService query, string? household, MetricDateRange range) =>
            await query.GetSummaryAsync(range.From, range.To, household));

        api.MapGet("/tokens", async (MetricsQueryService query, string? household, MetricDateRange range) =>
            await query.GetEventsAsync<TokenUsageEvent>(MetricKeys.Tokens, range.From, range.To, household));

        api.MapGet("/tools", async (MetricsQueryService query, string? household, MetricDateRange range) =>
            await query.GetEventsAsync<ToolCallEvent>(MetricKeys.Tools, range.From, range.To, household));

        api.MapGet("/errors", async (MetricsQueryService query, string? household, int? limit) =>
            await query.GetRecentErrorsAsync(limit ?? 100, household));

        api.MapGet("/errors/range", async (MetricsQueryService query, string? household, MetricDateRange range) =>
            await query.GetEventsAsync<ErrorEvent>(MetricKeys.Errors, range.From, range.To, household));

        api.MapGet("/schedules", async (MetricsQueryService query, string? household, MetricDateRange range) =>
            await query.GetEventsAsync<ScheduleExecutionEvent>(MetricKeys.Schedules, range.From, range.To, household));

        api.MapGet("/health", async (MetricsQueryService query, string? household) =>
            await query.GetHealthAsync(household));

        api.MapGet("/tokens/by-user", async (MetricsQueryService query, string? household, MetricDateRange range) =>
            await query.GetTokenBreakdownAsync("tokens:byUser:", range.From, range.To, household));

        api.MapGet("/tokens/by-model", async (MetricsQueryService query, string? household, MetricDateRange range) =>
            await query.GetTokenBreakdownAsync("tokens:byModel:", range.From, range.To, household));

        api.MapGet("/tokens/by/{dimension}", async (
            MetricsQueryService query,
            string? household,
            TokenDimension dimension,
            TokenMetric metric,
            MetricDateRange range) =>
            await query.GetTokenGroupedAsync(dimension, metric, range.From, range.To, household));

        api.MapGet("/tools/by/{dimension}", async (
            MetricsQueryService query,
            string? household,
            ToolDimension dimension,
            ToolMetric metric,
            MetricDateRange range) =>
            await query.GetToolGroupedAsync(dimension, metric, range.From, range.To, household));

        api.MapGet("/errors/by/{dimension}", async (
            MetricsQueryService query,
            string? household,
            ErrorDimension dimension,
            MetricDateRange range) =>
            await query.GetErrorGroupedAsync(dimension, range.From, range.To, household));

        api.MapGet("/schedules/by/{dimension}", async (
            MetricsQueryService query,
            string? household,
            ScheduleDimension dimension,
            MetricDateRange range) =>
            await query.GetScheduleGroupedAsync(dimension, range.From, range.To, household));

        api.MapGet("/memory/recall", async (MetricsQueryService query, string? household, MetricDateRange range) =>
            await query.GetEventsAsync<MemoryRecallEvent>(MetricKeys.MemoryRecall, range.From, range.To, household));

        api.MapGet("/memory/extraction", async (MetricsQueryService query, string? household, MetricDateRange range) =>
            await query.GetEventsAsync<MemoryExtractionEvent>(MetricKeys.MemoryExtraction, range.From, range.To, household));

        api.MapGet("/memory/dreaming", async (MetricsQueryService query, string? household, MetricDateRange range) =>
            await query.GetEventsAsync<MemoryDreamingEvent>(MetricKeys.MemoryDreaming, range.From, range.To, household));

        api.MapGet("/memory/judgments", async (MetricsQueryService query, string? household, MetricDateRange range) =>
            await query.GetEventsAsync<MemoryJudgmentEvent>(MetricKeys.MemoryJudgment, range.From, range.To, household));

        api.MapGet("/memory/by/{dimension}", async (
            MetricsQueryService query,
            string? household,
            MemoryDimension dimension,
            MemoryMetric metric,
            MetricDateRange range) =>
            await query.GetMemoryGroupedAsync(dimension, metric, range.From, range.To, household));

        api.MapGet("/latency", async (MetricsQueryService query, string? household, MetricDateRange range) =>
            await query.GetEventsAsync<LatencyEvent>(MetricKeys.Latency, range.From, range.To, household));

        api.MapGet("/latency/by/{dimension}", async (
            MetricsQueryService query,
            string? household,
            LatencyDimension dimension,
            Aggregation metric,
            MetricDateRange range) =>
            await query.GetLatencyGroupedAsync(dimension, metric, range.From, range.To, household));

        api.MapGet("/latency/trend", async (
            MetricsQueryService query,
            string? household,
            Aggregation metric,
            MetricDateRange range) =>
            await query.GetLatencyTrendAsync(metric, range.From, range.To, household));

        api.MapGet("/voice", async (MetricsQueryService query, string? household, MetricDateRange range) =>
            await query.GetEventsAsync<VoiceEvent>(MetricKeys.Voice, range.From, range.To, household));

        api.MapGet("/voice/by/{dimension}", async (
            MetricsQueryService query,
            string? household,
            VoiceDimension dimension,
            VoiceMetric metric,
            Aggregation? agg,
            MetricDateRange range) =>
            await query.GetVoiceGroupedAsync(dimension, metric, range.From, range.To, agg ?? Aggregation.Avg, household));

        api.MapGet("/skills", async (MetricsQueryService query, string? household, MetricDateRange range) =>
            await query.GetEventsAsync<SkillPreloadEvent>(MetricKeys.Skills, range.From, range.To, household));

        api.MapGet("/skills/by/{dimension}", async (
            MetricsQueryService query,
            string? household,
            SkillPreloadDimension dimension,
            SkillPreloadMetric metric,
            Aggregation? agg,
            MetricDateRange range) =>
            await query.GetSkillPreloadGroupedAsync(dimension, metric, range.From, range.To, agg ?? Aggregation.Avg, household));

        api.MapGet("/skills/trend", async (MetricsQueryService query, string? household, MetricDateRange range) =>
            await query.GetSkillPreloadTrendAsync(range.From, range.To, household));

        api.MapGet("/modals", async (MetricsQueryService query, string? household, MetricDateRange range) =>
            await query.GetEventsAsync<ModalDismissalEvent>(MetricKeys.Modals, range.From, range.To, household));

        api.MapGet("/modals/by/{dimension}", async (
            MetricsQueryService query,
            string? household,
            ModalDismissalDimension dimension,
            ModalDismissalMetric metric,
            Aggregation? agg,
            MetricDateRange range) =>
            await query.GetModalDismissalGroupedAsync(dimension, metric, range.From, range.To, agg ?? Aggregation.Avg, household));

        api.MapGet("/modals/trend", async (MetricsQueryService query, string? household, MetricDateRange range, string? kind) =>
            await query.GetModalDismissalTrendAsync(range.From, range.To, kind, household));

        api.MapGet("/households", async (MetricsQueryService query) =>
            await query.GetHouseholdsAsync());

        api.MapGet("/lemonade", async (MetricsQueryService query, string? household, MetricDateRange range) =>
            await query.GetLemonadeUsageAsync(range.From, range.To, household));

        api.MapGet("/lemonade/by-household", async (MetricsQueryService query, MetricDateRange range) =>
            await query.GetLemonadeByHouseholdAsync(range.From, range.To));

        // An owner's household alone, the one the gate verified (OperatorGate): no ?household= is read.
        app.MapGet(HouseholdUsage.Path, async (MetricsQueryService query, UsageOwner owner, MetricDateRange range) =>
            await query.GetHouseholdUsageAsync(owner.HouseholdId, range.From, range.To));
    }
}