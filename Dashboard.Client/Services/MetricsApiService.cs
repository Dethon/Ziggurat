using System.Net.Http.Json;
using Dashboard.Client.Metrics;
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

public record ServiceHealthResponse(string Service, bool IsHealthy, string LastSeen, string? Household = null);

// Every read is for the household the filter picked (everybody's when none), so a page shows that
// household alone without knowing there is a filter.
public sealed class MetricsApiService(HttpClient http, HouseholdFilter? households = null)
{
    private readonly HouseholdFilter _households = households ?? new HouseholdFilter();

    // The scopes something has been rolled up under — every household's id and the deployment's own —
    // whatever the filter says, since they are what it picks from.
    public Task<List<string>?> GetHouseholdsAsync() => http.GetFromJsonAsync<List<string>>("api/metrics/households");

    public Task<MetricsSummary?> GetSummaryAsync(DateOnly from, DateOnly to) =>
        Get<MetricsSummary>($"api/metrics/summary?from={from:yyyy-MM-dd}&to={to:yyyy-MM-dd}");

    public Task<List<TokenUsageEvent>?> GetTokensAsync(DateOnly from, DateOnly to) =>
        Get<List<TokenUsageEvent>>($"api/metrics/tokens?from={from:yyyy-MM-dd}&to={to:yyyy-MM-dd}");

    public Task<List<ToolCallEvent>?> GetToolsAsync(DateOnly from, DateOnly to) =>
        Get<List<ToolCallEvent>>($"api/metrics/tools?from={from:yyyy-MM-dd}&to={to:yyyy-MM-dd}");

    public Task<List<ErrorEvent>?> GetErrorsAsync(DateOnly from, DateOnly to) =>
        Get<List<ErrorEvent>>($"api/metrics/errors/range?from={from:yyyy-MM-dd}&to={to:yyyy-MM-dd}");

    public Task<List<ScheduleExecutionEvent>?> GetSchedulesAsync(DateOnly from, DateOnly to) =>
        Get<List<ScheduleExecutionEvent>>($"api/metrics/schedules?from={from:yyyy-MM-dd}&to={to:yyyy-MM-dd}");

    public Task<List<ServiceHealthResponse>?> GetHealthAsync() =>
        Get<List<ServiceHealthResponse>>("api/metrics/health");

    public Task<Dictionary<string, TValue>?> GetGroupedAsync<TValue>(
        string path,
        DateOnly from,
        DateOnly to,
        IReadOnlyList<(string Key, string Value)>? query = null)
    {
        var values = string.Concat((query ?? []).Select(q => $"{q.Key}={q.Value}&"));
        return Get<Dictionary<string, TValue>>(
            $"api/metrics/{path}?{values}from={from:yyyy-MM-dd}&to={to:yyyy-MM-dd}");
    }

    public Task<List<MemoryRecallEvent>?> GetMemoryRecallAsync(DateOnly from, DateOnly to) =>
        Get<List<MemoryRecallEvent>>($"api/metrics/memory/recall?from={from:yyyy-MM-dd}&to={to:yyyy-MM-dd}");

    public Task<List<MemoryExtractionEvent>?> GetMemoryExtractionAsync(DateOnly from, DateOnly to) =>
        Get<List<MemoryExtractionEvent>>($"api/metrics/memory/extraction?from={from:yyyy-MM-dd}&to={to:yyyy-MM-dd}");

    public Task<List<MemoryDreamingEvent>?> GetMemoryDreamingAsync(DateOnly from, DateOnly to) =>
        Get<List<MemoryDreamingEvent>>($"api/metrics/memory/dreaming?from={from:yyyy-MM-dd}&to={to:yyyy-MM-dd}");

    public Task<List<MemoryJudgmentEvent>?> GetMemoryJudgmentsAsync(DateOnly from, DateOnly to) =>
        Get<List<MemoryJudgmentEvent>>($"api/metrics/memory/judgments?from={from:yyyy-MM-dd}&to={to:yyyy-MM-dd}");

    public Task<List<LatencyEvent>?> GetLatencyAsync(DateOnly from, DateOnly to) =>
        Get<List<LatencyEvent>>($"api/metrics/latency?from={from:yyyy-MM-dd}&to={to:yyyy-MM-dd}");

    public Task<List<LatencyTrendSeries>?> GetLatencyTrendAsync(
        Aggregation aggregation, DateOnly from, DateOnly to) =>
        Get<List<LatencyTrendSeries>>(
            $"api/metrics/latency/trend?metric={aggregation}&from={from:yyyy-MM-dd}&to={to:yyyy-MM-dd}");

    public Task<List<VoiceEvent>?> GetVoiceEventsAsync(DateOnly from, DateOnly to) =>
        Get<List<VoiceEvent>>($"api/metrics/voice?from={from:yyyy-MM-dd}&to={to:yyyy-MM-dd}");

    public Task<List<SkillPreloadEvent>?> GetSkillPreloadEventsAsync(DateOnly from, DateOnly to) =>
        Get<List<SkillPreloadEvent>>($"api/metrics/skills?from={from:yyyy-MM-dd}&to={to:yyyy-MM-dd}");

    public Task<List<LatencyTrendSeries>?> GetSkillPreloadTrendAsync(DateOnly from, DateOnly to) =>
        Get<List<LatencyTrendSeries>>($"api/metrics/skills/trend?from={from:yyyy-MM-dd}&to={to:yyyy-MM-dd}");

    public Task<List<ModalDismissalEvent>?> GetModalDismissalEventsAsync(DateOnly from, DateOnly to) =>
        Get<List<ModalDismissalEvent>>($"api/metrics/modals?from={from:yyyy-MM-dd}&to={to:yyyy-MM-dd}");

    public Task<List<LatencyTrendSeries>?> GetModalDismissalTrendAsync(DateOnly from, DateOnly to, string? kind) =>
        Get<List<LatencyTrendSeries>>(
            $"api/metrics/modals/trend?from={from:yyyy-MM-dd}&to={to:yyyy-MM-dd}" + (kind is null ? "" : $"&kind={Uri.EscapeDataString(kind)}"));

    private Task<T?> Get<T>(string path) => http.GetFromJsonAsync<T>(_households.On(path));
}