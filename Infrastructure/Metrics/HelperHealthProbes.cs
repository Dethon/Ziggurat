using Domain.Contracts;
using Domain.DTOs.Metrics;
using Microsoft.Extensions.Hosting;

namespace Infrastructure.Metrics;

// A helper container a household runs beside one of its servers (Home Assistant beside its server,
// qBittorrent beside the library) has no tunnel, so nothing of the deployment can reach it to ask.
// The server beside it can: it probes each helper and beats that helper's heartbeat on its own feed
// while it answers, so the helper is a tile of its own that goes red when it stops — the reading the
// deployment's own probes give a container (HttpHealthProbeService): any HTTP answer means it is up.
public sealed class HelperHealthProbes(
    IMetricsPublisher publisher,
    HttpClient http,
    IReadOnlyDictionary<string, string> helpers) : BackgroundService
{
    public const string ClientName = "helper-health";
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    // The heartbeat's own cadence, so a helper's tile lives exactly as long as its server's.
    private static readonly TimeSpan _interval = TimeSpan.FromSeconds(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.WhenAll(helpers.Select(h => ProbeAsync(h.Key, h.Value, stoppingToken)));
            await Task.Delay(_interval, stoppingToken);
        }
    }

    private async Task ProbeAsync(string helper, string url, CancellationToken ct)
    {
        try
        {
            using var _ = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            publisher.Publish(new HeartbeatEvent { Service = helper });
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            // Not answering is the reading: no heartbeat, and the tile goes red when the last expires.
        }
    }
}