using System.Net;
using System.Text;
using Dashboard.Client.Services;
using Domain.DTOs.Metrics.Enums;
using Shouldly;

namespace Tests.Unit.Dashboard.Client.Services;

public class MetricsApiServiceLatencyTests
{
    private sealed class CapturingHandler : HttpMessageHandler
    {
        public string LastUri = "";
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            LastUri = request.RequestUri!.ToString();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("null", Encoding.UTF8, "application/json")
            });
        }
    }

    [Fact]
    public async Task LatencyClientMethods_CallExpectedRoutes()
    {
        var handler = new CapturingHandler();
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") };
        var api = new MetricsApiService(http);
        await api.GetLatencyAsync(7);
        handler.LastUri.ShouldContain("api/metrics/latency?days=7");

        // latency/by is a grouped breakdown like every other family's, asserted in
        // MetricsApiServiceGroupedTests. The trend is latency's alone.
        await api.GetLatencyTrendAsync(Aggregation.P95, 7);
        handler.LastUri.ShouldContain("api/metrics/latency/trend?metric=P95");
    }
}