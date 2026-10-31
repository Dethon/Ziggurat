using System.Collections.Concurrent;
using Domain.Contracts;
using Domain.DTOs.Channel;
using Domain.DTOs.Voice;
using Mcp.Hosting;

namespace McpServerTimers.Services;

// Timers ringing on their owners' channels. This server holds no channel, so a ring goes into its
// inbox for the agent, which offers it wherever the person is; what it keeps is which are still
// ringing, by household, so ./dismiss can stop them and report them as it reports a satellite's.
// The agent settles one here when it ended elsewhere — dismissed on a channel, or rung out — and an
// agent's restart forgets nothing it needs: an alert it never took was never kept.
public sealed class ChannelAlerts(ChannelNotificationEmitter emitter) : IChannelAlerts
{
    private readonly ConcurrentDictionary<string, Ringing> _ringing = new();

    public async Task<bool> RingAsync(ArmedTimer timer, CancellationToken ct)
    {
        // Create refuses a timer with no target and no owner, so a fire without one has no person
        // to ring for and is not rung.
        if (timer.Owner is not { } owner)
        {
            return false;
        }

        var alert = new ChannelAlertNotification
        {
            AlertId = Guid.NewGuid().ToString("N"),
            Owner = owner,
            Text = timer.Text ?? $"{timer.Id} timer",
            Kind = AnnounceKind.Timer,
            AgentId = timer.AgentId
        };

        // Held before it is raised, so a dismiss that lands while it is being taken still finds it.
        _ringing[alert.AlertId] = new Ringing(timer.TenantId, new DismissedAlert(alert.Text, alert.Kind));
        if (await emitter.EmitAlertAsync(alert, ct))
        {
            return true;
        }

        _ringing.TryRemove(alert.AlertId, out _);
        return false;
    }

    public async Task<IReadOnlyList<DismissedAlert>> StopAllAsync(string tenantId, CancellationToken ct)
    {
        var stopped = _ringing
            .Where(entry => entry.Value.TenantId == tenantId)
            .Where(entry => _ringing.TryRemove(entry.Key, out _))
            .ToList();

        await Task.WhenAll(stopped.Select(entry =>
            emitter.EmitAlertStoppedAsync(new ChannelAlertStoppedNotification { AlertId = entry.Key }, ct)));
        return [.. stopped.Select(entry => entry.Value.Alert)];
    }

    // It ended on a channel, so nothing here holds it as ringing any more.
    public void Settle(string alertId) => _ringing.TryRemove(alertId, out _);

    private sealed record Ringing(string TenantId, DismissedAlert Alert);
}