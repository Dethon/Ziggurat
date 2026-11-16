using System.Net;
using Domain.Contracts;
using Domain.DTOs.Metrics;
using Infrastructure.Agents.ChatClients;
using Infrastructure.Clients;
using Infrastructure.Metrics;
using Shouldly;

namespace Tests.Unit.Infrastructure.Metrics;

// A household's own Lemonade box is a dashboard tile of that household's, read from what its Keys page
// saved rather than from the deployment's .env (user, 2026-11-13): the agent, which already reaches
// every household's box, asks each for its health and beats while it answers — any answer, as a probed
// container's is read — so a box that stops answering goes red when its last beat expires.
public class LemonadeBoxHealthProbesTests
{
    private sealed class RecordingPublisher : IMetricsPublisher
    {
        private readonly List<MetricEvent> _events = [];

        public IReadOnlyList<HeartbeatEvent> Beats
        {
            get
            {
                lock (_events)
                {
                    return [.. _events.OfType<HeartbeatEvent>()];
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

    private sealed class Answering(Func<Uri, HttpResponseMessage?> answer) : HttpMessageHandler
    {
        public List<Uri> Asked { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            lock (Asked)
            {
                Asked.Add(request.RequestUri!);
            }

            return answer(request.RequestUri!) is { } response
                ? Task.FromResult(response)
                : throw new HttpRequestException("connection refused");
        }
    }

    private static LemonadeHost Box(string url, HttpMessageHandler? transport = null) =>
        new(new LemonadeChatHostOptions { ApiUrl = url }, FixedLemonadeModelSource.None, transport);

    [Fact]
    public async Task EachHouseholdsBox_BeatsUnderItsHousehold_WhileItAnswersAnything()
    {
        var publisher = new RecordingPublisher();
        var shared = new Answering(uri => uri.Host == "box.home.lan" ? new HttpResponseMessage(HttpStatusCode.NotFound) : null);
        var kit = new Answering(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var probes = new LemonadeBoxHealthProbes(
            () =>
            [
                new HouseholdLemonadeTarget("home", Box("http://box.home.lan:13305/api/v1")),
                new HouseholdLemonadeTarget("casa", Box("https://box.casa.example/api/v1")),
                new HouseholdLemonadeTarget("vidal", Box("http://lemonade.vidal.tunnel", kit))
            ],
            publisher, shared);

        await probes.ProbeOnceAsync(CancellationToken.None);

        publisher.Beats.Select(b => (b.Service, b.TenantId)).ShouldBe(
            [(LemonadeBoxHealthProbes.Service, "home"), (LemonadeBoxHealthProbes.Service, "vidal")], ignoreOrder: true);
        shared.Asked.ShouldContain(new Uri("http://box.home.lan:13305/api/v1/health"));
        kit.Asked.ShouldBe([new Uri("http://lemonade.vidal.tunnel/health")], "a kit's box is asked through its own tunnel");
    }
}