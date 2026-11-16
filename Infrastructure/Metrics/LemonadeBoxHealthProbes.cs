using Domain.Contracts;
using Domain.DTOs.Metrics;
using Infrastructure.Agents.ChatClients;
using Microsoft.Extensions.Hosting;

namespace Infrastructure.Metrics;

// Each household's own Lemonade box, as its Keys page saved it, is a dashboard tile of that household's
// (user, 2026-11-13): the agent reaches every household's box already — one at an address directly,
// one behind a router through its kit's forwarder — so it asks each for its health and beats a
// heartbeat under the household while it answers, any answer, as a probed container's is read
// (HttpHealthProbeService). Not answering is the reading: the tile goes red when the last beat expires.
// The deployment's own box, from its .env, is still Observability's to probe.
public sealed class LemonadeBoxHealthProbes(
    Func<IReadOnlyList<HouseholdLemonadeTarget>> boxes,
    IMetricsPublisher publisher,
    HttpMessageHandler sharedTransport) : BackgroundService
{
    // The tile's name, the one Observability's probe of the deployment's box went by.
    public const string Service = "lemonade-chat-host";

    private static readonly TimeSpan _timeout = TimeSpan.FromSeconds(5);

    // The heartbeat's own cadence: a beat lives sixty seconds, so two probes stand behind each.
    private static readonly TimeSpan _interval = TimeSpan.FromSeconds(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await ProbeOnceAsync(stoppingToken);
            await Task.Delay(_interval, stoppingToken);
        }
    }

    public Task ProbeOnceAsync(CancellationToken ct) => Task.WhenAll(boxes().Select(box => ProbeAsync(box, ct)));

    private async Task ProbeAsync(HouseholdLemonadeTarget target, CancellationToken ct)
    {
        try
        {
            using var http = new HttpClient(target.Box.Transport ?? sharedTransport, disposeHandler: false) { Timeout = _timeout };
            using var _ = await http.GetAsync($"{target.Box.Options.Address}/health", HttpCompletionOption.ResponseHeadersRead, ct);
            publisher.Publish(new HeartbeatEvent { Service = Service, TenantId = target.Household });
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
        }
    }
}