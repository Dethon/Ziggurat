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

    public Task<MetricsSummary?> GetSummaryAsync(int days) =>
        Get<MetricsSummary>($"api/metrics/summary?days={days}");

    public Task<List<TokenUsageEvent>?> GetTokensAsync(int days) =>
        Get<List<TokenUsageEvent>>($"api/metrics/tokens?days={days}");

    public Task<List<ToolCallEvent>?> GetToolsAsync(int days) =>
        Get<List<ToolCallEvent>>($"api/metrics/tools?days={days}");

    public Task<List<ErrorEvent>?> GetErrorsAsync(int days) =>
        Get<List<ErrorEvent>>($"api/metrics/errors/range?days={days}");

    public Task<List<ScheduleExecutionEvent>?> GetSchedulesAsync(int days) =>
        Get<List<ScheduleExecutionEvent>>($"api/metrics/schedules?days={days}");

    public Task<List<ServiceHealthResponse>?> GetHealthAsync() =>
        Get<List<ServiceHealthResponse>>("api/metrics/health");

    public Task<Dictionary<string, TValue>?> GetGroupedAsync<TValue>(
        string path,
        int days,
        IReadOnlyList<(string Key, string Value)>? query = null)
    {
        var values = string.Concat((query ?? []).Select(q => $"{q.Key}={q.Value}&"));
        return Get<Dictionary<string, TValue>>(
            $"api/metrics/{path}?{values}days={days}");
    }

    public Task<List<MemoryRecallEvent>?> GetMemoryRecallAsync(int days) =>
        Get<List<MemoryRecallEvent>>($"api/metrics/memory/recall?days={days}");

    public Task<List<MemoryExtractionEvent>?> GetMemoryExtractionAsync(int days) =>
        Get<List<MemoryExtractionEvent>>($"api/metrics/memory/extraction?days={days}");

    public Task<List<MemoryDreamingEvent>?> GetMemoryDreamingAsync(int days) =>
        Get<List<MemoryDreamingEvent>>($"api/metrics/memory/dreaming?days={days}");

    public Task<List<MemoryJudgmentEvent>?> GetMemoryJudgmentsAsync(int days) =>
        Get<List<MemoryJudgmentEvent>>($"api/metrics/memory/judgments?days={days}");

    public Task<List<LatencyEvent>?> GetLatencyAsync(int days) =>
        Get<List<LatencyEvent>>($"api/metrics/latency?days={days}");

    public Task<List<LatencyTrendSeries>?> GetLatencyTrendAsync(
        Aggregation aggregation, int days) =>
        Get<List<LatencyTrendSeries>>(
            $"api/metrics/latency/trend?metric={aggregation}&days={days}");

    public Task<List<VoiceEvent>?> GetVoiceEventsAsync(int days) =>
        Get<List<VoiceEvent>>($"api/metrics/voice?days={days}");

    public Task<List<SkillPreloadEvent>?> GetSkillPreloadEventsAsync(int days) =>
        Get<List<SkillPreloadEvent>>($"api/metrics/skills?days={days}");

    public Task<List<LatencyTrendSeries>?> GetSkillPreloadTrendAsync(int days) =>
        Get<List<LatencyTrendSeries>>($"api/metrics/skills/trend?days={days}");

    public Task<List<ModalDismissalEvent>?> GetModalDismissalEventsAsync(int days) =>
        Get<List<ModalDismissalEvent>>($"api/metrics/modals?days={days}");

    public Task<List<LatencyTrendSeries>?> GetModalDismissalTrendAsync(int days, string? kind) =>
        Get<List<LatencyTrendSeries>>(
            $"api/metrics/modals/trend?days={days}" + (kind is null ? "" : $"&kind={Uri.EscapeDataString(kind)}"));

    private Task<T?> Get<T>(string path) => http.GetFromJsonAsync<T>(_households.On(path));
}