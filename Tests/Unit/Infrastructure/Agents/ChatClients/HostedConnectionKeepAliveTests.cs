using System.Net;
using Domain.Contracts;
using Domain.DTOs.Metrics;
using Infrastructure.Agents.ChatClients;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Shouldly;

namespace Tests.Unit.Infrastructure.Agents.ChatClients;

public class HostedConnectionKeepAliveTests
{
    private readonly FakeTimeProvider _time = new();
    private readonly RecordingPublisher _metrics = new();

    [Fact]
    public async Task FiresOnItsInterval_WithoutWaitingForRealTime()
    {
        var handler = new RecordingHandler(HttpStatusCode.OK);
        using var keepAlive = CreateKeepAlive(handler);

        await StartAsync(keepAlive);
        handler.Requests.ShouldBeEmpty();

        await AdvanceOneIntervalAsync(handler, expectedRequests: 1);
        await AdvanceOneIntervalAsync(handler, expectedRequests: 2);

        await keepAlive.StopAsync(CancellationToken.None);
    }

    [Fact]
    public void FiresInsideTheIdleTimeoutItExistsToBeat()
    {
        HostedConnectionKeepAliveOptions.DefaultInterval.ShouldBeLessThan(HostedConnectionPool.IdleTimeout);
    }

    [Fact]
    public async Task TargetsANonBillableEndpoint()
    {
        var handler = new RecordingHandler(HttpStatusCode.OK);
        using var keepAlive = CreateKeepAlive(handler);

        await StartAsync(keepAlive);
        await AdvanceOneIntervalAsync(handler, expectedRequests: 1);
        await keepAlive.StopAsync(CancellationToken.None);

        var request = handler.Requests[0];
        request.Method.ShouldBe(HttpMethod.Get);
        request.RequestUri!.AbsolutePath.ShouldBe("/api/v1/key");
        // A completion would cost money on every fire for no user-visible work.
        request.RequestUri.AbsolutePath.ShouldNotContain("completions");
    }

    [Fact]
    public async Task ABaseAddressWithoutATrailingSlash_StillPingsUnderIt()
    {
        var handler = new RecordingHandler(HttpStatusCode.OK);
        using var keepAlive = new HostedConnectionKeepAlive(
            new HttpClient(handler),
            new HostedConnectionKeepAliveOptions
            {
                BaseAddress = "https://hosted.invalid/api/v1",
                ApiKey = "sk-test"
            },
            _metrics,
            _time,
            NullLogger<HostedConnectionKeepAlive>.Instance);

        await StartAsync(keepAlive);
        await AdvanceOneIntervalAsync(handler, expectedRequests: 1);
        await keepAlive.StopAsync(CancellationToken.None);

        handler.Requests[0].RequestUri!.AbsolutePath.ShouldBe("/api/v1/key");
    }

    [Fact]
    public async Task WhenTheKeepAliveFails_PublishesAMetricAndKeepsRunning()
    {
        var handler = new RecordingHandler(HttpStatusCode.ServiceUnavailable);
        using var keepAlive = CreateKeepAlive(handler);

        await StartAsync(keepAlive);
        await AdvanceOneIntervalAsync(handler, expectedRequests: 1);

        _metrics.Events.OfType<ErrorEvent>()
            .ShouldContain(e => e.Service == HostedConnectionKeepAlive.MetricService);

        // The host process is still standing, so the next interval still fires.
        await AdvanceOneIntervalAsync(handler, expectedRequests: 2);

        await keepAlive.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task WithNoKeyConfigured_DoesNotRunAtAll()
    {
        var handler = new RecordingHandler(HttpStatusCode.OK);
        using var keepAlive = CreateKeepAlive(handler, apiKey: null);

        await StartAsync(keepAlive);
        _time.Advance(HostedConnectionKeepAliveOptions.DefaultInterval * 3);
        await Eventually.Settle();
        await keepAlive.StopAsync(CancellationToken.None);

        handler.Requests.ShouldBeEmpty();
        _metrics.Events.OfType<ErrorEvent>().ShouldBeEmpty();
    }

    private HostedConnectionKeepAlive CreateKeepAlive(HttpMessageHandler handler, string? apiKey = "sk-test")
    {
        return new HostedConnectionKeepAlive(
            new HttpClient(handler),
            new HostedConnectionKeepAliveOptions
            {
                BaseAddress = "https://hosted.invalid/api/v1/",
                ApiKey = apiKey
            },
            _metrics,
            _time,
            NullLogger<HostedConnectionKeepAlive>.Instance);
    }

    private static async Task StartAsync(HostedConnectionKeepAlive keepAlive)
    {
        await keepAlive.StartAsync(CancellationToken.None);
        await keepAlive.Armed.WaitAsync(TimeSpan.FromSeconds(5));
    }

    private async Task AdvanceOneIntervalAsync(RecordingHandler handler, int expectedRequests)
    {
        _time.Advance(HostedConnectionKeepAliveOptions.DefaultInterval);
        await WaitFor(() => handler.Requests.Count >= expectedRequests);
        handler.Requests.Count.ShouldBe(expectedRequests);
    }

    private static async Task WaitFor(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (!condition() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }
    }

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

    private sealed class RecordingHandler(HttpStatusCode status) : HttpMessageHandler
    {
        private readonly List<HttpRequestMessage> _requests = [];

        public IReadOnlyList<HttpRequestMessage> Requests
        {
            get
            {
                lock (_requests)
                {
                    return _requests.ToList();
                }
            }
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            lock (_requests)
            {
                _requests.Add(request);
            }

            return Task.FromResult(new HttpResponseMessage(status));
        }
    }
}