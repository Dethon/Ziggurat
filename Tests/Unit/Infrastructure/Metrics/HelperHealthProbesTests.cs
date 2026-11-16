using System.Net;
using Domain.Contracts;
using Domain.DTOs.Metrics;
using Infrastructure.Metrics;
using Shouldly;

namespace Tests.Unit.Infrastructure.Metrics;

// A household's helper containers (Home Assistant, Music Assistant, the speech extractor, the
// download tools, the browser) have no tunnel of their own, so the server beside each reports it
// with its own heartbeat: a tile per helper, red when the helper stops answering.
public class HelperHealthProbesTests
{
    private sealed class RecordingPublisher : IMetricsPublisher
    {
        private readonly List<MetricEvent> _events = [];

        public IReadOnlyList<MetricEvent> Events
        {
            get
            {
                lock (_events)
                {
                    return [.. _events];
                }
            }
        }

        public void Publish(MetricEvent metricEvent)
        {
            lock (_events)
            {
                _events.Add(metricEvent);
            }
        }
    }

    // Any answer means the container is up and listening, as the deployment's own probes read it:
    // Home Assistant answers its root with a redirect, qBittorrent with 401.
    private sealed class Helpers(params string[] answering) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            answering.Contains(request.RequestUri!.Host)
                ? Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized))
                : throw new HttpRequestException("Connection refused");
    }

    [Fact]
    public async Task AHelperThatAnswers_IsReportedByItsName_AndOneThatDoesNotIsNot()
    {
        var publisher = new RecordingPublisher();
        var sut = new HelperHealthProbes(
            publisher,
            new HttpClient(new Helpers("qbittorrent")),
            new Dictionary<string, string>
            {
                ["qbittorrent"] = "http://qbittorrent:8001/",
                ["jackett"] = "http://jackett:9117/"
            });

        await sut.StartAsync(CancellationToken.None);
        await Eventually.Until(
            () => publisher.Events.OfType<HeartbeatEvent>().Any(e => e.Service == "qbittorrent"),
            "the answering helper's heartbeat");
        await sut.StopAsync(CancellationToken.None);

        publisher.Events.OfType<HeartbeatEvent>().Select(e => e.Service).ShouldNotContain("jackett");
    }
}