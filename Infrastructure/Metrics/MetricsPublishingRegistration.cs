using Domain.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace Infrastructure.Metrics;

public static class MetricsPublishingRegistration
{
    // Being a metrics-publishing host is one call. Nothing here is separable: a host that resolved
    // the sink as its caller-facing publisher would turn every publish into a live Redis round
    // trip, and a host that publishes without a heartbeat is missing from the health roster.
    public static IServiceCollection AddMetricsPublishing(this IServiceCollection services, string serviceName) =>
        services.AddMetricsPublishing(serviceName, new MetricsFeedConfig());

    // A host given a feed URL (METRICS__URL: a household server's metrics back-call) posts there
    // instead of publishing onto Redis, so it reaches no deployment database; everything else is the
    // same one call.
    public static IServiceCollection AddMetricsPublishing(
        this IServiceCollection services, string serviceName, MetricsFeedConfig feed)
    {
        if (feed.Url.Length > 0)
        {
            services.AddHttpClient(HttpMetricSink.ClientName, http =>
            {
                http.BaseAddress = new Uri(feed.Url);
                http.Timeout = feed.RequestTimeout;
            });
        }

        return services
            .AddSingleton<IMetricSink>(sp => feed.Url.Length > 0
                ? new HttpMetricSink(sp.GetRequiredService<IHttpClientFactory>().CreateClient(HttpMetricSink.ClientName))
                : new RedisMetricSink(sp.GetRequiredService<IConnectionMultiplexer>()))
            .AddSingleton<IMetricsPublisher>(sp => new BufferedMetricsPublisher(
                sp.GetRequiredService<IMetricSink>(),
                sp.GetService<ILogger<BufferedMetricsPublisher>>()))
            .AddHostedService(sp =>
                new HeartbeatService(sp.GetRequiredService<IMetricsPublisher>(), serviceName));
    }
}

// Where a host's metric events go: the deployment's Redis feed unless a URL is given, which a
// household's server sets to its metrics back-call (`http://127.0.0.1:<port>/events`).
public sealed record MetricsFeedConfig
{
    public string Url { get; init; } = "";

    // Bounds one post, so a tunnel that is down drops an event rather than holding the buffer.
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(5);
}