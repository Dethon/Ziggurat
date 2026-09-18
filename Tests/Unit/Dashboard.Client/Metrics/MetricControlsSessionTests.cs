using Dashboard.Client.Effects;
using Dashboard.Client.Metrics;
using Dashboard.Client.Services;
using Dashboard.Client.State.Errors;
using Dashboard.Client.State.Health;
using Dashboard.Client.State.Latency;
using Dashboard.Client.State.Memory;
using Dashboard.Client.State.Metrics;
using Dashboard.Client.State.Schedules;
using Dashboard.Client.State.Skills;
using Dashboard.Client.State.Tokens;
using Dashboard.Client.State.Tools;
using Dashboard.Client.State.Voice;
using Dashboard.Client.State.Web;
using Domain.DTOs.Metrics;
using Domain.DTOs.Metrics.Enums;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Tests.Unit.Dashboard.Client.Fixtures;

namespace Tests.Unit.Dashboard.Client.Metrics;

public class MetricControlsSessionTests : IDisposable
{
    private static readonly DateOnly _today = new(2026, 3, 24);

    private readonly FakeApiHandler _handler = new();
    private readonly FakeJsRuntime _js = new();
    private readonly TokensStore _tokensStore = new();
    private readonly ToolsStore _toolsStore = new();
    private readonly ErrorsStore _errorsStore = new();
    private readonly SchedulesStore _schedulesStore = new();
    private readonly MemoryStore _memoryStore = new();
    private readonly LatencyStore _latencyStore = new();
    private readonly VoiceStore _voiceStore = new();
    private readonly MetricsStore _metricsStore = new();
    private readonly HealthStore _healthStore = new();
    private readonly LocalStorageService _storage;
    private readonly MetricFamilyTable _families;
    private readonly DataLoadEffect _dataLoad;

    public MetricControlsSessionTests()
    {
        var http = new HttpClient(_handler) { BaseAddress = new Uri("http://localhost") };
        var api = new MetricsApiService(http);
        _storage = new LocalStorageService(_js);
        _families = new MetricFamilyTable(
            api, _tokensStore, _toolsStore, _errorsStore, _schedulesStore,
            _memoryStore, _latencyStore, _voiceStore, new SkillsStore(), new WebStore());
        var binder = new MetricsHubBinder(_families, _metricsStore, _healthStore, NullLogger<MetricsHubBinder>.Instance);
        _dataLoad = new DataLoadEffect(
            _families, new OverviewFigures(api, _metricsStore, _healthStore), binder);
    }

    public void Dispose()
    {
        _tokensStore.Dispose();
        _toolsStore.Dispose();
        _errorsStore.Dispose();
        _schedulesStore.Dispose();
        _memoryStore.Dispose();
        _latencyStore.Dispose();
        _voiceStore.Dispose();
        _metricsStore.Dispose();
        _healthStore.Dispose();
    }

    private MetricControlsSession SessionFor(MetricFamily family, params MetricChoice[] extraChoices) =>
        new(family, extraChoices, _storage, new FakeTimeProvider(
            new DateTimeOffset(_today, TimeOnly.MinValue, TimeSpan.Zero)), _dataLoad);

    // Every family, by the preference keys its page has always used.
    public static TheoryData<string, string, string?> Families => new()
    {
        { "tokens", nameof(TokenDimension.Agent), nameof(TokenMetric.Cost) },
        { "tools", nameof(ToolDimension.Status), nameof(ToolMetric.CallCount) },
        { "errors", nameof(ErrorDimension.ErrorType), null },
        { "schedules", nameof(ScheduleDimension.Status), null },
        { "memory", nameof(MemoryDimension.Agent), nameof(MemoryMetric.AvgDuration) },
        { "latency", nameof(LatencyDimension.Model), nameof(Aggregation.P50) },
        { "voice", nameof(VoiceDimension.Room), nameof(VoiceMetric.SttLatencyMs) },
    };

    private MetricFamily FamilyNamed(string name) => _families.All.Single(f => f.Name == name);

    [Theory]
    [MemberData(nameof(Families))]
    public async Task InitializeAsync_APreferenceIsSaved_AppliesItToTheFamilysChoices(
        string name, string savedGroupBy, string? savedMetric)
    {
        var family = FamilyNamed(name);
        _js.Storage[$"{name}.groupBy"] = savedGroupBy;
        if (savedMetric is not null)
        {
            _js.Storage[$"{name}.metric"] = savedMetric;
        }

        await SessionFor(family).InitializeAsync();

        family.Dimension.Current.ShouldBe(savedGroupBy);
        family.Metric?.Current.ShouldBe(savedMetric);
    }

    [Theory]
    [MemberData(nameof(Families))]
    public async Task ChangeAsync_APillMoves_PersistsUnderTheFamilysPrefixAndRefreshes(
        string name, string chosenGroupBy, string? _)
    {
        var family = FamilyNamed(name);
        var session = SessionFor(family);
        await session.InitializeAsync();
        _handler.Requests.Clear();
        // Latency's refresh is two requests; the rest are one.
        _handler.EnqueueResponse(new Dictionary<string, decimal>(), delay: TimeSpan.Zero);
        _handler.EnqueueResponse(new List<LatencyTrendSeries>(), delay: TimeSpan.Zero);

        await session.ChangeAsync(family.Dimension, chosenGroupBy);

        _js.Storage[$"{name}.groupBy"].ShouldBe(chosenGroupBy);
        family.Dimension.Current.ShouldBe(chosenGroupBy);
        _handler.Requests.ShouldContain(u => u != null && u.Contains($"/{name}", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(1, "2026-03-24")]
    [InlineData(7, "2026-03-18")]
    [InlineData(30, "2026-02-23")]
    public async Task ChangeDaysAsync_ADayCountIsChosen_DerivesTheRangeFromTheTimeProvider(
        int days, string expectedFrom)
    {
        var session = SessionFor(_families.Tokens);
        await session.InitializeAsync();

        await session.ChangeDaysAsync(days.ToString());

        session.SelectedDays.ShouldBe(days);
        session.From.ShouldBe(DateOnly.Parse(expectedFrom));
        session.To.ShouldBe(_today);
        _tokensStore.State.From.ShouldBe(DateOnly.Parse(expectedFrom));
    }

    // A page's time pill moves that page's family, and nothing else. Voice keeps the thirty days its
    // own page persisted while the tokens page is showing today.
    [Fact]
    public async Task ChangeDaysAsync_ADayCountIsChosen_StampsOnlyTheSessionsOwnFamily()
    {
        var chosenOnTheVoicePage = new DateOnly(2026, 2, 23);
        _voiceStore.SetDateRange(chosenOnTheVoicePage, _today);
        var session = SessionFor(_families.Tokens);
        await session.InitializeAsync();

        await session.ChangeDaysAsync("1");

        _tokensStore.State.From.ShouldBe(_today);
        _voiceStore.State.From.ShouldBe(chosenOnTheVoicePage);
    }

    [Fact]
    public async Task InitializeAsync_ADayCountIsSaved_DerivesTheRangeFromIt()
    {
        _js.Storage["tokens.days"] = "7";

        var session = SessionFor(_families.Tokens);
        await session.InitializeAsync();

        session.SelectedDays.ShouldBe(7);
        session.From.ShouldBe(new DateOnly(2026, 3, 18));
    }

    // Voice's aggregate pill is the one choice the shared header does not own, so it is the one at
    // risk of being saved and never read back.
    [Fact]
    public async Task InitializeAsync_TheVoiceAggregationIsSaved_AppliesIt()
    {
        var aggregation = MetricChoice.For("agg", () => _voiceStore.State.Agg, _voiceStore.SetAgg);
        _js.Storage["voice.agg"] = nameof(Aggregation.P95);

        await SessionFor(_families.Voice, aggregation).InitializeAsync();

        _voiceStore.State.Agg.ShouldBe(Aggregation.P95);
    }

    // Voice's aggregate pill is the one choice the shared header does not own. It is persisted and
    // refreshed the same way, and the refresh still carries the aggregation the user picked.
    [Fact]
    public async Task ChangeAsync_TheVoiceAggregation_PersistsAndRefreshesWithIt()
    {
        var aggregation = MetricChoice.For("agg", () => _voiceStore.State.Agg, _voiceStore.SetAgg);
        var session = SessionFor(_families.Voice, aggregation);
        await session.InitializeAsync();
        _handler.Requests.Clear();
        _handler.EnqueueResponse(new Dictionary<string, decimal>(), delay: TimeSpan.Zero);

        await session.ChangeAsync(aggregation, nameof(Aggregation.P95));

        _js.Storage["voice.agg"].ShouldBe(nameof(Aggregation.P95));
        _handler.Requests.ShouldContain(u => u != null && u.Contains("agg=P95", StringComparison.Ordinal));
    }

    // The guard swaps the metric when a group-by makes it invalid. The swap has to be persisted
    // with the group-by, or the next visit restores the disallowed combination and selects a
    // disabled pill.
    [Fact]
    public async Task ChangeAsync_AGroupByGuardSwapsTheMetric_TheSwapSurvivesTheNextVisit()
    {
        var session = SessionFor(_families.Tools);
        await session.InitializeAsync();
        _handler.EnqueueResponse(new Dictionary<string, decimal>(), delay: TimeSpan.Zero);
        await session.ChangeAsync(_families.Tools.Metric!, nameof(ToolMetric.ErrorRate));
        _handler.EnqueueResponse(new Dictionary<string, decimal>(), delay: TimeSpan.Zero);

        await session.ChangeAsync(_families.Tools.Dimension, nameof(ToolDimension.Status));

        _js.Storage["tools.metric"].ShouldBe(nameof(ToolMetric.CallCount));

        await SessionFor(_families.Tools).InitializeAsync();

        _toolsStore.State.GroupBy.ShouldBe(ToolDimension.Status);
        _toolsStore.State.Metric.ShouldBe(ToolMetric.CallCount);
    }

    // A pill click during an API outage: the choice sticks and is saved, the breakdown keeps its
    // last known value, and nothing escapes into Blazor's unhandled-error UI.
    [Fact]
    public async Task ChangeAsync_TheRefreshFails_ThePillStillMovesAndNothingEscapes()
    {
        var lastKnown = new Dictionary<string, decimal> { ["kept"] = 42m };
        _tokensStore.SetBreakdown(lastKnown);
        var session = SessionFor(_families.Tokens);
        await session.InitializeAsync();
        // Nothing is staged, so the refresh's request answers 404 and the family throws.

        await session.ChangeAsync(_families.Tokens.Dimension, nameof(TokenDimension.Agent));

        _js.Storage["tokens.groupBy"].ShouldBe(nameof(TokenDimension.Agent));
        _tokensStore.State.GroupBy.ShouldBe(TokenDimension.Agent);
        _tokensStore.State.Breakdown.ShouldBe(lastKnown);
    }

    // An older build could persist the disallowed pair itself. Restoring the dimension first and
    // the metric second used to re-apply the stale metric on top of the coercion, rendering a
    // disabled-yet-selected pill. The restore must end valid, and the coercion must be saved so the
    // stale pair cannot come back next visit.
    [Fact]
    public async Task InitializeAsync_AnOlderBuildSavedADisallowedCombination_RestoresAValidOneAndPersistsIt()
    {
        _js.Storage["tools.groupBy"] = nameof(ToolDimension.Status);
        _js.Storage["tools.metric"] = nameof(ToolMetric.ErrorRate);

        await SessionFor(_families.Tools).InitializeAsync();

        _toolsStore.State.GroupBy.ShouldBe(ToolDimension.Status);
        _toolsStore.State.Metric.ShouldBe(ToolMetric.CallCount);
        _js.Storage["tools.metric"].ShouldBe(nameof(ToolMetric.CallCount));
    }

    [Fact]
    public async Task InitializeAsync_APreferenceNoLongerParses_LeavesTheChoiceAlone()
    {
        _js.Storage["tokens.groupBy"] = "SomethingRetired";

        await SessionFor(_families.Tokens).InitializeAsync();

        _families.Tokens.Dimension.Current.ShouldBe(nameof(TokenDimension.User));
    }
}