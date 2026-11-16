using System.Net.Http.Json;
using System.Text.Json;
using Domain.DTOs.Metrics;

namespace Infrastructure.Metrics;

// The feed reached over HTTP rather than Redis: a household's server posts each event on its metrics
// back-call, and the relay publishes it onto the deployment's feed (BackCallGateway). The JSON is the
// feed's own, so the relay reads it as the dashboard does.
public sealed class HttpMetricSink(HttpClient http) : IMetricSink
{
    public const string ClientName = "metrics-feed";

    private static readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);

    public async Task SendAsync(MetricEvent metricEvent, CancellationToken ct = default)
    {
        using var response = await http.PostAsJsonAsync("", metricEvent, _jsonOptions, ct);
        response.EnsureSuccessStatusCode();
    }
}