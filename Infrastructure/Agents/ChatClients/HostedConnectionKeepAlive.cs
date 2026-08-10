using System.Net.Http.Headers;
using Domain.Contracts;
using Domain.DTOs.Metrics;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Infrastructure.Agents.ChatClients;

public record HostedConnectionKeepAliveOptions
{
    // Below HostedConnectionPool.IdleTimeout, so a connection is never left idle long enough
    // for the pool to drop it. Raised lifetimes alone are not enough: at 35 turns a day most
    // gaps are longer than any idle timeout worth configuring.
    //
    // The interval also sets how long the pool sits cold after ConnectionLifetime expires a
    // connection, because the next ping is what re-establishes it: a fraction of
    // interval / (lifetime + interval) of the time, and whatever calls first inside that
    // window pays the handshake. Shortening the interval buys that fraction down at a cost
    // in pings, and 30s (2/min) is where the two stop being worth trading.
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromSeconds(30);

    public required string BaseAddress { get; init; }
    public string? ApiKey { get; init; }
    public TimeSpan Interval { get; init; } = DefaultInterval;
}

// Holds one connection to the hosted provider open through the long gaps between turns, so
// the LLM call on the next turn does not pay a fresh TCP+TLS handshake.
public sealed class HostedConnectionKeepAlive : BackgroundService
{
    public const string MetricService = "hosted-connection-keepalive";

    // Returns the key's own metadata and consumes no tokens. It must never become a
    // completion: that would cost money on every fire for no user-visible work.
    private const string NonBillableEndpoint = "key";

    private readonly HttpClient _httpClient;
    private readonly HostedConnectionKeepAliveOptions _options;
    private readonly IMetricsPublisher _metricsPublisher;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<HostedConnectionKeepAlive> _logger;
    private readonly TaskCompletionSource _armed =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    // Completes once the interval is running, or once the keep-alive has decided it has
    // nothing to do. The host starts background services without waiting for them to reach
    // their first await, so a test driving a fake clock needs to know when the clock matters.
    internal Task Armed => _armed.Task;

    public HostedConnectionKeepAlive(
        HttpClient httpClient,
        HostedConnectionKeepAliveOptions options,
        IMetricsPublisher metricsPublisher,
        TimeProvider timeProvider,
        ILogger<HostedConnectionKeepAlive> logger)
    {
        _httpClient = httpClient;
        _options = options;
        _metricsPublisher = metricsPublisher;
        _timeProvider = timeProvider;
        _logger = logger;

        // Without the trailing slash the relative "key" replaces the last segment, so an
        // apiUrl configured as ".../api/v1" would ping /api/key and 404 on every fire.
        httpClient.BaseAddress = new Uri(options.BaseAddress.TrimEnd('/') + "/");
        if (!string.IsNullOrWhiteSpace(options.ApiKey))
        {
            httpClient.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", options.ApiKey);
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (string.IsNullOrWhiteSpace(_options.ApiKey))
        {
            _logger.LogDebug("No hosted provider key configured, connection keep-alive is idle");
            _armed.TrySetResult();
            return;
        }

        // The first tick lands one interval in, so startup is not delayed, and cancellation
        // ends the wait rather than being observed one interval late.
        using var timer = new PeriodicTimer(_options.Interval, _timeProvider);
        _armed.TrySetResult();
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            await PingAsync(stoppingToken);
        }
    }

    private async Task PingAsync(CancellationToken ct)
    {
        try
        {
            using var response = await _httpClient.GetAsync(NonBillableEndpoint, ct);
            response.EnsureSuccessStatusCode();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            // A failed keep-alive means the pool is going cold again, which would silently
            // give back the handshake win. It is a metric, never a crash.
            _logger.LogWarning(ex, "Hosted connection keep-alive failed");
            _metricsPublisher.Publish(new ErrorEvent
            {
                Service = MetricService,
                ErrorType = ex.GetType().Name,
                Message = $"Keep-alive failed: {ex.Message}"
            });
        }
    }
}