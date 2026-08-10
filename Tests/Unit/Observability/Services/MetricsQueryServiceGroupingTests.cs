using System.Text.Json;
using Domain.DTOs.Metrics;
using Domain.DTOs.Metrics.Enums;
using Moq;
using Observability.Services;
using Shouldly;
using StackExchange.Redis;

namespace Tests.Unit.Observability.Services;

public class MetricsQueryServiceGroupingTests
{
    private readonly Mock<IDatabase> _db = new();
    private readonly Mock<IConnectionMultiplexer> _redis = new();
    private readonly MetricsQueryService _sut;

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public MetricsQueryServiceGroupingTests()
    {
        _redis.Setup(r => r.GetDatabase(It.IsAny<int>(), It.IsAny<object>()))
            .Returns(_db.Object);
        _sut = new MetricsQueryService(_redis.Object);
    }

    private void SetupSortedSet(string key, IEnumerable<MetricEvent> events)
    {
        var entries = events
            .Select(e => new RedisValue(JsonSerializer.Serialize(e, _jsonOptions)))
            .ToArray();
        _db.Setup(d => d.SortedSetRangeByScoreAsync(key, It.IsAny<double>(), It.IsAny<double>(),
                It.IsAny<Exclude>(), It.IsAny<Order>(), It.IsAny<long>(), It.IsAny<long>(),
                It.IsAny<CommandFlags>()))
            .ReturnsAsync(entries);
    }

    [Theory]
    [InlineData(TokenDimension.User, TokenMetric.Tokens, "alice", 450.0, "bob", 75.0)]
    [InlineData(TokenDimension.Model, TokenMetric.Cost, "gpt-4", 0.3, "claude-3", 0.05)]
    [InlineData(TokenDimension.Agent, TokenMetric.Tokens, "agent-1", 450.0, "unknown", 75.0)]
    public async Task GetTokenGroupedAsync_GroupsByDimensionAndMetric(
        TokenDimension dimension, TokenMetric metric,
        string keyA, double expectedA, string keyB, double expectedB)
    {
        var date = new DateOnly(2026, 3, 15);
        SetupSortedSet("metrics:tokens:2026-03-15",
        [
            new TokenUsageEvent { Sender = "alice", Model = "gpt-4", InputTokens = 100, OutputTokens = 50, Cost = 0.1m, AgentId = "agent-1" },
            new TokenUsageEvent { Sender = "alice", Model = "gpt-4", InputTokens = 200, OutputTokens = 100, Cost = 0.2m, AgentId = "agent-1" },
            new TokenUsageEvent { Sender = "bob", Model = "claude-3", InputTokens = 50, OutputTokens = 25, Cost = 0.05m, AgentId = null }
        ]);

        var result = await _sut.GetTokenGroupedAsync(dimension, metric, date, date);

        result[keyA].ShouldBe((decimal)expectedA);
        result[keyB].ShouldBe((decimal)expectedB);
    }

    [Theory]
    [InlineData(ToolDimension.ToolName, ToolMetric.CallCount)]
    [InlineData(ToolDimension.ToolName, ToolMetric.AvgDuration)]
    [InlineData(ToolDimension.ToolName, ToolMetric.ErrorRate)]
    [InlineData(ToolDimension.Status, ToolMetric.CallCount)]
    public async Task GetToolGroupedAsync_GroupsByDimensionAndMetric(
        ToolDimension dimension, ToolMetric metric)
    {
        var date = new DateOnly(2026, 3, 15);
        SetupSortedSet("metrics:tools:2026-03-15",
        [
            new ToolCallEvent { ToolName = "search", DurationMs = 100, Success = true },
            new ToolCallEvent { ToolName = "search", DurationMs = 300, Success = false },
            new ToolCallEvent { ToolName = "search", DurationMs = 200, Success = false },
            new ToolCallEvent { ToolName = "read_file", DurationMs = 50, Success = true }
        ]);

        var result = await _sut.GetToolGroupedAsync(dimension, metric, date, date);

        switch (dimension, metric)
        {
            case (ToolDimension.ToolName, ToolMetric.CallCount):
                result["search"].ShouldBe(3m);
                result["read_file"].ShouldBe(1m);
                break;
            case (ToolDimension.ToolName, ToolMetric.AvgDuration):
                result["search"].ShouldBe(200m);   // (100+300+200)/3
                result["read_file"].ShouldBe(50m);
                break;
            case (ToolDimension.ToolName, ToolMetric.ErrorRate):
                result["search"].ShouldBeInRange(66.66m, 66.67m); // 2/3
                result["read_file"].ShouldBe(0m);
                break;
            case (ToolDimension.Status, ToolMetric.CallCount):
                result["Success"].ShouldBe(2m);
                result["Failure"].ShouldBe(2m);
                break;
        }
    }

    [Theory]
    [InlineData(ErrorDimension.Service, "Agent", 2, "Observability", 1)]
    [InlineData(ErrorDimension.ErrorType, "NullReference", 2, "Timeout", 1)]
    public async Task GetErrorGroupedAsync_GroupsByDimension(
        ErrorDimension dimension, string keyA, int expectedA, string keyB, int expectedB)
    {
        var date = new DateOnly(2026, 3, 15);
        SetupSortedSet("metrics:errors:2026-03-15",
        [
            new ErrorEvent { Service = "Agent", ErrorType = "NullReference", Message = "oops" },
            new ErrorEvent { Service = "Agent", ErrorType = "Timeout", Message = "timed out" },
            new ErrorEvent { Service = "Observability", ErrorType = "NullReference", Message = "oops2" }
        ]);

        var result = await _sut.GetErrorGroupedAsync(dimension, date, date);

        result[keyA].ShouldBe(expectedA);
        result[keyB].ShouldBe(expectedB);
    }

    [Theory]
    [InlineData(ScheduleDimension.Schedule, "daily-report", 2, "weekly-summary", 1)]
    [InlineData(ScheduleDimension.Status, "Success", 2, "Failure", 1)]
    public async Task GetScheduleGroupedAsync_GroupsByDimension(
        ScheduleDimension dimension, string keyA, int expectedA, string keyB, int expectedB)
    {
        var date = new DateOnly(2026, 3, 15);
        SetupSortedSet("metrics:schedules:2026-03-15",
        [
            new ScheduleExecutionEvent { ScheduleId = "daily-report", Prompt = "...", DurationMs = 1000, Success = true },
            new ScheduleExecutionEvent { ScheduleId = "daily-report", Prompt = "...", DurationMs = 1200, Success = false },
            new ScheduleExecutionEvent { ScheduleId = "weekly-summary", Prompt = "...", DurationMs = 2000, Success = true }
        ]);

        var result = await _sut.GetScheduleGroupedAsync(dimension, date, date);

        result[keyA].ShouldBe(expectedA);
        result[keyB].ShouldBe(expectedB);
    }

    [Theory]
    [InlineData(MemoryDimension.User, MemoryMetric.Count)]
    [InlineData(MemoryDimension.EventType, MemoryMetric.Count)]
    [InlineData(MemoryDimension.User, MemoryMetric.AvgDuration)]
    [InlineData(MemoryDimension.EventType, MemoryMetric.StoredCount)]
    [InlineData(MemoryDimension.Agent, MemoryMetric.MergedCount)]
    public async Task GetMemoryGroupedAsync_GroupsByDimensionAndMetric(
        MemoryDimension dimension, MemoryMetric metric)
    {
        var date = new DateOnly(2026, 3, 15);
        SetupSortedSet("metrics:memory-recall:2026-03-15",
        [
            new MemoryRecallEvent { DurationMs = 100, MemoryCount = 5, UserId = "alice" },
            new MemoryRecallEvent { DurationMs = 300, MemoryCount = 3, UserId = "alice" },
        ]);
        SetupSortedSet("metrics:memory-extraction:2026-03-15",
        [
            new MemoryExtractionEvent { DurationMs = 1000, CandidateCount = 8, StoredCount = 3, UserId = "alice" },
            new MemoryExtractionEvent { DurationMs = 2000, CandidateCount = 12, StoredCount = 5, UserId = "bob" },
        ]);
        SetupSortedSet("metrics:memory-dreaming:2026-03-15",
        [
            new MemoryDreamingEvent { MergedCount = 5, DecayedCount = 2, ProfileRegenerated = true, UserId = "alice", AgentId = "agent-1" },
            new MemoryDreamingEvent { MergedCount = 3, DecayedCount = 1, ProfileRegenerated = false, UserId = "bob", AgentId = "agent-1" },
            new MemoryDreamingEvent { MergedCount = 7, DecayedCount = 4, ProfileRegenerated = true, UserId = "alice", AgentId = null },
        ]);

        var result = await _sut.GetMemoryGroupedAsync(dimension, metric, date, date);

        switch (dimension, metric)
        {
            case (MemoryDimension.User, MemoryMetric.Count):
                // alice: 2 recalls + 1 extraction + 2 dreamings = 5
                // bob:   1 extraction + 1 dreaming = 2
                result["alice"].ShouldBe(5m);
                result["bob"].ShouldBe(2m);
                break;
            case (MemoryDimension.EventType, MemoryMetric.Count):
                result["Recall"].ShouldBe(2m);
                result["Extraction"].ShouldBe(2m);
                result["Dreaming"].ShouldBe(3m);
                break;
            case (MemoryDimension.User, MemoryMetric.AvgDuration):
                // alice durations: 100, 300 (recall), 1000 (extraction) — dreaming has no duration
                // (100 + 300 + 1000) / 3 = 466.666...
                result["alice"].ShouldBeInRange(466.66m, 466.67m);
                break;
            case (MemoryDimension.EventType, MemoryMetric.StoredCount):
                result["Extraction"].ShouldBe(8m); // 3 + 5
                result["Recall"].ShouldBe(0m);     // recalls have no StoredCount
                break;
            case (MemoryDimension.Agent, MemoryMetric.MergedCount):
                result["agent-1"].ShouldBe(8m); // 5 + 3
                result["unknown"].ShouldBe(7m);
                break;
        }
    }

    [Theory]
    [InlineData(new[] { 10, 20, 30, 40, 100 }, 50, 30)]
    [InlineData(new[] { 10, 20, 30, 40, 100 }, 95, 100)]
    [InlineData(new[] { 10, 20, 30, 40, 100 }, 99, 100)]
    [InlineData(new int[0], 95, 0)]
    [InlineData(new[] { 7 }, 95, 7)]
    public void ComputePercentile_ReturnsExpected(int[] values, int q, int expected)
    {
        var decimalValues = values.Select(v => (decimal)v).ToArray();
        MetricsQueryService.ComputePercentile(decimalValues, q).ShouldBe(expected);
    }

    [Theory]
    [InlineData(LatencyDimension.Stage, Aggregation.P95, "LlmTotal", 5000.0, "MemoryRecall", 40.0)]
    [InlineData(LatencyDimension.Model, Aggregation.Avg, "m1", 1800.0, "unknown", 45.0)]
    public async Task GetLatencyGroupedAsync_GroupsByDimensionAndMetric(
        LatencyDimension dimension, Aggregation metric,
        string keyA, double expectedA, string keyB, double expectedB)
    {
        var date = new DateOnly(2026, 3, 15);
        SetupSortedSet("metrics:latency:2026-03-15",
        [
            new LatencyEvent { Stage = LatencyStage.LlmTotal, DurationMs = 100, Model = "m1" },
            new LatencyEvent { Stage = LatencyStage.LlmTotal, DurationMs = 300, Model = "m1" },
            new LatencyEvent { Stage = LatencyStage.LlmTotal, DurationMs = 5000, Model = "m1" },
            new LatencyEvent { Stage = LatencyStage.MemoryRecall, DurationMs = 40 },
            new LatencyEvent { Stage = LatencyStage.SessionWarmup, DurationMs = 50 },
        ]);

        var result = await _sut.GetLatencyGroupedAsync(dimension, metric, date, date);

        result[keyA].ShouldBe((decimal)expectedA);
        result[keyB].ShouldBe((decimal)expectedB);
    }

    [Theory]
    [InlineData(VoiceDimension.SatelliteId, VoiceMetric.UtteranceTranscribed)]
    [InlineData(VoiceDimension.Room, VoiceMetric.UtteranceTranscribed)]
    [InlineData(VoiceDimension.Identity, VoiceMetric.SttLatencyMs)]
    [InlineData(VoiceDimension.SatelliteId, VoiceMetric.WakeToFirstAudioMs)]
    [InlineData(VoiceDimension.SatelliteId, VoiceMetric.TseLatencyMs)]
    public async Task GetVoiceGroupedAsync_GroupsByDimensionAndMetric(
        VoiceDimension dimension, VoiceMetric metric)
    {
        var date = new DateOnly(2026, 3, 15);
        SetupSortedSet("metrics:voice:2026-03-15",
        [
            new VoiceEvent { Metric = VoiceMetric.UtteranceTranscribed, SatelliteId = "kitchen-01", Room = "Kitchen", Identity = "household" },
            new VoiceEvent { Metric = VoiceMetric.UtteranceTranscribed, SatelliteId = "kitchen-01", Room = "Kitchen", Identity = "household" },
            new VoiceEvent { Metric = VoiceMetric.UtteranceTranscribed, SatelliteId = "office-01", Room = "Office", Identity = "household" },
            new VoiceEvent { Metric = VoiceMetric.SttLatencyMs, SatelliteId = "kitchen-01", Room = "Kitchen", Identity = "household", DurationMs = 100 },
            new VoiceEvent { Metric = VoiceMetric.SttLatencyMs, SatelliteId = "kitchen-01", Room = "Kitchen", Identity = "household", DurationMs = 300 },
            new VoiceEvent { Metric = VoiceMetric.WakeToFirstAudioMs, SatelliteId = "kitchen-01", Room = "Kitchen", Identity = "household", DurationMs = 200 },
            new VoiceEvent { Metric = VoiceMetric.WakeToFirstAudioMs, SatelliteId = "kitchen-01", Room = "Kitchen", Identity = "household", DurationMs = 400 },
            new VoiceEvent { Metric = VoiceMetric.TseLatencyMs, SatelliteId = "kitchen-01", Room = "Kitchen", Identity = "household", DurationMs = 50 },
            new VoiceEvent { Metric = VoiceMetric.TseLatencyMs, SatelliteId = "kitchen-01", Room = "Kitchen", Identity = "household", DurationMs = 150 },
        ]);

        var result = await _sut.GetVoiceGroupedAsync(dimension, metric, date, date);

        switch (dimension, metric)
        {
            case (VoiceDimension.SatelliteId, VoiceMetric.UtteranceTranscribed):
                result["kitchen-01"].ShouldBe(2m);   // count branch
                result["office-01"].ShouldBe(1m);
                break;
            case (VoiceDimension.Room, VoiceMetric.UtteranceTranscribed):
                result["Kitchen"].ShouldBe(2m);
                result["Office"].ShouldBe(1m);
                break;
            case (VoiceDimension.Identity, VoiceMetric.SttLatencyMs):
                result["household"].ShouldBe(200m);   // avg branch: avg(100,300)
                break;
            case (VoiceDimension.SatelliteId, VoiceMetric.WakeToFirstAudioMs):
                result["kitchen-01"].ShouldBe(300m);  // avg branch: avg(200,400)
                break;
            case (VoiceDimension.SatelliteId, VoiceMetric.TseLatencyMs):
                result["kitchen-01"].ShouldBe(100m);  // avg branch: avg(50,150), not a raw event count (2)
                break;
        }
    }

    [Fact]
    public async Task GetVoiceGroupedAsync_NewDurationMetric_AveragesInsteadOfCounting()
    {
        var date = new DateOnly(2026, 3, 15);
        SetupSortedSet("metrics:voice:2026-03-15",
        [
            new VoiceEvent { Metric = VoiceMetric.SpeechEndToFirstAudioMs, Room = "office", DurationMs = 1000 },
            new VoiceEvent { Metric = VoiceMetric.SpeechEndToFirstAudioMs, Room = "office", DurationMs = 3000 },
        ]);

        var result = await _sut.GetVoiceGroupedAsync(
            VoiceDimension.Room, VoiceMetric.SpeechEndToFirstAudioMs, date, date);

        // Regression guard: the old hard-coded switch listed only four duration metrics, so every
        // newly appended ...Ms member silently reported a count (here it would have been 2).
        result["office"].ShouldBe(2000m);
    }

    [Fact]
    public async Task GetVoiceGroupedAsync_HonoursRequestedAggregation()
    {
        var date = new DateOnly(2026, 3, 15);
        SetupSortedSet("metrics:voice:2026-03-15",
        [
            new VoiceEvent { Metric = VoiceMetric.EndpointTailMs, Room = "office", DurationMs = 1000 },
            new VoiceEvent { Metric = VoiceMetric.EndpointTailMs, Room = "office", DurationMs = 9000 },
        ]);

        var result = await _sut.GetVoiceGroupedAsync(
            VoiceDimension.Room, VoiceMetric.EndpointTailMs, date, date, Aggregation.Max);

        result["office"].ShouldBe(9000m);
    }

    [Fact]
    public async Task GetVoiceGroupedAsync_NullDimensionValue_BucketsAsUnknown()
    {
        var date = new DateOnly(2026, 3, 15);
        SetupSortedSet("metrics:voice:2026-03-15",
        [
            new VoiceEvent { Metric = VoiceMetric.UtteranceTranscribed, SatelliteId = null, Room = null, Identity = null },
        ]);

        var result = await _sut.GetVoiceGroupedAsync(VoiceDimension.Room, VoiceMetric.UtteranceTranscribed, date, date);

        result["(unknown)"].ShouldBe(1m);
    }

    [Theory]
    [InlineData("tokens")]
    [InlineData("memory")]
    [InlineData("latency")]
    [InlineData("voice")]
    public async Task GetGroupedAsync_EmptyData_ReturnsEmptyDictionary(string query)
    {
        var date = new DateOnly(2026, 3, 15);
        SetupSortedSet("metrics:tokens:2026-03-15", []);
        SetupSortedSet("metrics:memory-recall:2026-03-15", []);
        SetupSortedSet("metrics:memory-extraction:2026-03-15", []);
        SetupSortedSet("metrics:memory-dreaming:2026-03-15", []);
        SetupSortedSet("metrics:latency:2026-03-15", []);
        SetupSortedSet("metrics:voice:2026-03-15", []);

        var result = query switch
        {
            "tokens" => await _sut.GetTokenGroupedAsync(TokenDimension.User, TokenMetric.Tokens, date, date),
            "memory" => await _sut.GetMemoryGroupedAsync(MemoryDimension.User, MemoryMetric.Count, date, date),
            "latency" => await _sut.GetLatencyGroupedAsync(LatencyDimension.Stage, Aggregation.P95, date, date),
            "voice" => await _sut.GetVoiceGroupedAsync(VoiceDimension.SatelliteId, VoiceMetric.UtteranceTranscribed, date, date),
            _ => throw new ArgumentOutOfRangeException(nameof(query))
        };

        result.ShouldBeEmpty();
    }

    [Fact]
    public async Task GetLatencyTrendAsync_ShortRange_BucketsHourlyPerStage()
    {
        var date = new DateOnly(2026, 3, 15);
        SetupSortedSet("metrics:latency:2026-03-15",
        [
            new LatencyEvent { Stage = LatencyStage.LlmTotal, DurationMs = 100,
                Timestamp = new DateTimeOffset(2026, 3, 15, 10, 5, 0, TimeSpan.Zero) },
            new LatencyEvent { Stage = LatencyStage.LlmTotal, DurationMs = 300,
                Timestamp = new DateTimeOffset(2026, 3, 15, 10, 50, 0, TimeSpan.Zero) },
            new LatencyEvent { Stage = LatencyStage.LlmTotal, DurationMs = 999,
                Timestamp = new DateTimeOffset(2026, 3, 15, 11, 1, 0, TimeSpan.Zero) },
        ]);

        var result = await _sut.GetLatencyTrendAsync(Aggregation.Avg, date, date);

        var series = result.Single(s => s.Stage == "LlmTotal");
        series.Points.Count.ShouldBe(2);
        series.Points[0].Bucket.ShouldBe(new DateTimeOffset(2026, 3, 15, 10, 0, 0, TimeSpan.Zero));
        series.Points[0].Value.ShouldBe(200m);   // avg(100,300)
        series.Points[1].Bucket.ShouldBe(new DateTimeOffset(2026, 3, 15, 11, 0, 0, TimeSpan.Zero));
        series.Points[1].Value.ShouldBe(999m);
    }

    [Fact]
    public async Task GetLatencyTrendAsync_LongRange_BucketsDailyPerStage()
    {
        var from = new DateOnly(2026, 3, 15);
        var to = new DateOnly(2026, 3, 20);
        SetupSortedSet("metrics:latency:2026-03-15",
        [
            new LatencyEvent { Stage = LatencyStage.LlmTotal, DurationMs = 100,
                Timestamp = new DateTimeOffset(2026, 3, 15, 9, 5, 0, TimeSpan.Zero) },
            new LatencyEvent { Stage = LatencyStage.LlmTotal, DurationMs = 300,
                Timestamp = new DateTimeOffset(2026, 3, 15, 22, 50, 0, TimeSpan.Zero) },
        ]);
        SetupSortedSet("metrics:latency:2026-03-18",
        [
            new LatencyEvent { Stage = LatencyStage.LlmTotal, DurationMs = 999,
                Timestamp = new DateTimeOffset(2026, 3, 18, 3, 1, 0, TimeSpan.Zero) },
        ]);

        var result = await _sut.GetLatencyTrendAsync(Aggregation.Avg, from, to);

        var series = result.Single(s => s.Stage == "LlmTotal");
        series.Points.Count.ShouldBe(2);
        series.Points[0].Bucket.ShouldBe(new DateTimeOffset(2026, 3, 15, 0, 0, 0, TimeSpan.Zero));
        series.Points[0].Value.ShouldBe(200m);   // avg(100,300)
        series.Points[1].Bucket.ShouldBe(new DateTimeOffset(2026, 3, 18, 0, 0, 0, TimeSpan.Zero));
        series.Points[1].Value.ShouldBe(999m);
    }
}