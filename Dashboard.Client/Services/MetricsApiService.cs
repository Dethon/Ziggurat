using System.Net.Http.Json;
using Domain.DTOs.Metrics;
using Domain.DTOs.Metrics.Enums;

namespace Dashboard.Client.Services;

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

public record ServiceHealthResponse(string Service, bool IsHealthy, string LastSeen);

public sealed class MetricsApiService(HttpClient http)
{
    public Task<MetricsSummary?> GetSummaryAsync(DateOnly from, DateOnly to) =>
        http.GetFromJsonAsync<MetricsSummary>($"api/metrics/summary?from={from:yyyy-MM-dd}&to={to:yyyy-MM-dd}");

    public Task<List<TokenUsageEvent>?> GetTokensAsync(DateOnly from, DateOnly to) =>
        http.GetFromJsonAsync<List<TokenUsageEvent>>($"api/metrics/tokens?from={from:yyyy-MM-dd}&to={to:yyyy-MM-dd}");

    public Task<List<ToolCallEvent>?> GetToolsAsync(DateOnly from, DateOnly to) =>
        http.GetFromJsonAsync<List<ToolCallEvent>>($"api/metrics/tools?from={from:yyyy-MM-dd}&to={to:yyyy-MM-dd}");

    public Task<List<ErrorEvent>?> GetErrorsAsync(DateOnly from, DateOnly to) =>
        http.GetFromJsonAsync<List<ErrorEvent>>($"api/metrics/errors/range?from={from:yyyy-MM-dd}&to={to:yyyy-MM-dd}");

    public Task<List<ScheduleExecutionEvent>?> GetSchedulesAsync(DateOnly from, DateOnly to) =>
        http.GetFromJsonAsync<List<ScheduleExecutionEvent>>($"api/metrics/schedules?from={from:yyyy-MM-dd}&to={to:yyyy-MM-dd}");

    public Task<List<ServiceHealthResponse>?> GetHealthAsync() =>
        http.GetFromJsonAsync<List<ServiceHealthResponse>>("api/metrics/health");

    public Task<Dictionary<string, TValue>?> GetGroupedAsync<TValue>(
        string path,
        DateOnly from,
        DateOnly to,
        IReadOnlyList<(string Key, string Value)>? query = null)
    {
        var values = string.Concat((query ?? []).Select(q => $"{q.Key}={q.Value}&"));
        return http.GetFromJsonAsync<Dictionary<string, TValue>>(
            $"api/metrics/{path}?{values}from={from:yyyy-MM-dd}&to={to:yyyy-MM-dd}");
    }

    public Task<List<MemoryRecallEvent>?> GetMemoryRecallAsync(DateOnly from, DateOnly to) =>
        http.GetFromJsonAsync<List<MemoryRecallEvent>>($"api/metrics/memory/recall?from={from:yyyy-MM-dd}&to={to:yyyy-MM-dd}");

    public Task<List<MemoryExtractionEvent>?> GetMemoryExtractionAsync(DateOnly from, DateOnly to) =>
        http.GetFromJsonAsync<List<MemoryExtractionEvent>>($"api/metrics/memory/extraction?from={from:yyyy-MM-dd}&to={to:yyyy-MM-dd}");

    public Task<List<MemoryDreamingEvent>?> GetMemoryDreamingAsync(DateOnly from, DateOnly to) =>
        http.GetFromJsonAsync<List<MemoryDreamingEvent>>($"api/metrics/memory/dreaming?from={from:yyyy-MM-dd}&to={to:yyyy-MM-dd}");

    public Task<List<MemoryJudgmentEvent>?> GetMemoryJudgmentsAsync(DateOnly from, DateOnly to) =>
        http.GetFromJsonAsync<List<MemoryJudgmentEvent>>($"api/metrics/memory/judgments?from={from:yyyy-MM-dd}&to={to:yyyy-MM-dd}");

    public Task<List<LatencyEvent>?> GetLatencyAsync(DateOnly from, DateOnly to) =>
        http.GetFromJsonAsync<List<LatencyEvent>>($"api/metrics/latency?from={from:yyyy-MM-dd}&to={to:yyyy-MM-dd}");

    public Task<List<LatencyTrendSeries>?> GetLatencyTrendAsync(
        Aggregation aggregation, DateOnly from, DateOnly to) =>
        http.GetFromJsonAsync<List<LatencyTrendSeries>>(
            $"api/metrics/latency/trend?metric={aggregation}&from={from:yyyy-MM-dd}&to={to:yyyy-MM-dd}");

    public Task<List<VoiceEvent>?> GetVoiceEventsAsync(DateOnly from, DateOnly to) =>
        http.GetFromJsonAsync<List<VoiceEvent>>($"api/metrics/voice?from={from:yyyy-MM-dd}&to={to:yyyy-MM-dd}");

    public Task<List<SkillPreloadEvent>?> GetSkillPreloadEventsAsync(DateOnly from, DateOnly to) =>
        http.GetFromJsonAsync<List<SkillPreloadEvent>>($"api/metrics/skills?from={from:yyyy-MM-dd}&to={to:yyyy-MM-dd}");

    public Task<List<LatencyTrendSeries>?> GetSkillPreloadTrendAsync(DateOnly from, DateOnly to) =>
        http.GetFromJsonAsync<List<LatencyTrendSeries>>($"api/metrics/skills/trend?from={from:yyyy-MM-dd}&to={to:yyyy-MM-dd}");

    public Task<List<ModalDismissalEvent>?> GetModalDismissalEventsAsync(DateOnly from, DateOnly to) =>
        http.GetFromJsonAsync<List<ModalDismissalEvent>>($"api/metrics/modals?from={from:yyyy-MM-dd}&to={to:yyyy-MM-dd}");

    public Task<List<LatencyTrendSeries>?> GetModalDismissalTrendAsync(DateOnly from, DateOnly to) =>
        http.GetFromJsonAsync<List<LatencyTrendSeries>>($"api/metrics/modals/trend?from={from:yyyy-MM-dd}&to={to:yyyy-MM-dd}");
}