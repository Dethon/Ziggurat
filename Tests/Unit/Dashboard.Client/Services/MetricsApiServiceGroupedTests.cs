using System.Net;
using System.Text;
using Dashboard.Client.Services;
using Domain.DTOs.Metrics.Enums;
using Shouldly;

namespace Tests.Unit.Dashboard.Client.Services;

public class MetricsApiServiceGroupedTests
{
    private sealed class CapturingHandler : HttpMessageHandler
    {
        public string LastUri = "";

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            LastUri = request.RequestUri!.ToString();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json")
            });
        }
    }

    private readonly CapturingHandler _handler = new();
    private readonly MetricsApiService _api;

    private static readonly DateOnly _from = new(2026, 3, 1);
    private static readonly DateOnly _to = new(2026, 3, 2);

    public MetricsApiServiceGroupedTests()
    {
        var http = new HttpClient(_handler) { BaseAddress = new Uri("http://localhost/") };
        _api = new MetricsApiService(http);
    }

    // The seven families' breakdown requests, exactly as they went out before there was one call
    // that built them.
    public static TheoryData<string, string, (string, string)[], string> GroupedRoutes => new()
    {
        {
            "tokens", $"tokens/by/{TokenDimension.Model}", [("metric", nameof(TokenMetric.Cost))],
            "http://localhost/api/metrics/tokens/by/Model?metric=Cost&from=2026-03-01&to=2026-03-02"
        },
        {
            "tools", $"tools/by/{ToolDimension.ToolName}", [("metric", nameof(ToolMetric.CallCount))],
            "http://localhost/api/metrics/tools/by/ToolName?metric=CallCount&from=2026-03-01&to=2026-03-02"
        },
        {
            "errors", $"errors/by/{ErrorDimension.Service}", [],
            "http://localhost/api/metrics/errors/by/Service?from=2026-03-01&to=2026-03-02"
        },
        {
            "schedules", $"schedules/by/{ScheduleDimension.Schedule}", [],
            "http://localhost/api/metrics/schedules/by/Schedule?from=2026-03-01&to=2026-03-02"
        },
        {
            "memory", $"memory/by/{MemoryDimension.User}", [("metric", nameof(MemoryMetric.Count))],
            "http://localhost/api/metrics/memory/by/User?metric=Count&from=2026-03-01&to=2026-03-02"
        },
        {
            "latency", $"latency/by/{LatencyDimension.Stage}", [("metric", nameof(Aggregation.P95))],
            "http://localhost/api/metrics/latency/by/Stage?metric=P95&from=2026-03-01&to=2026-03-02"
        },
        {
            "voice", $"voice/by/{VoiceDimension.Room}",
            [("metric", nameof(VoiceMetric.SttLatencyMs)), ("agg", nameof(Aggregation.P95))],
            "http://localhost/api/metrics/voice/by/Room?metric=SttLatencyMs&agg=P95&from=2026-03-01&to=2026-03-02"
        },
    };

    [Theory]
    [MemberData(nameof(GroupedRoutes))]
    public async Task GetGroupedAsync_AnyFamily_RequestsTheSameUrlItAlwaysHas(
        string _, string path, (string, string)[] query, string expectedUri)
    {
        await _api.GetGroupedAsync<decimal>(path, _from, _to, query);

        _handler.LastUri.ShouldBe(expectedUri);
    }
}