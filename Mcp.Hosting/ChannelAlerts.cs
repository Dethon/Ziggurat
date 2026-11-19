using System.Collections.Concurrent;
using Domain.Contracts;
using Domain.DTOs.Channel;
using Domain.DTOs.Voice;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;

namespace Mcp.Hosting;

// Alerts ringing on their owners' channels, raised by a server that holds no channel — the timers
// server's timers, Sugary's glucose alarms. A ring goes into the server's inbox for the agent, which
// offers it wherever the person is; what is kept here is which are still ringing, by household, so
// the server's own stop finds them and reports them as it reports a satellite's. The agent settles
// one here when it ended elsewhere — dismissed on a channel, or rung out — and an agent's restart
// forgets nothing it needs: an alert it never took was never kept.
public sealed class ChannelAlerts(ChannelNotificationEmitter emitter) : IChannelAlerts
{
    private readonly ConcurrentDictionary<string, Ringing> _ringing = new();

    public async Task<bool> RingAsync(ChannelAlertRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var alert = new ChannelAlertNotification
        {
            AlertId = Guid.NewGuid().ToString("N"),
            Owner = request.Owner,
            Text = request.Text,
            Kind = request.Kind,
            AgentId = request.AgentId
        };

        // Held before it is raised, so a stop that lands while it is being taken still finds it.
        _ringing[alert.AlertId] = new Ringing(request.TenantId, new DismissedAlert(alert.Text, alert.Kind));
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

public static class ChannelAlertsExtensions
{
    // A channel server that raises alerts for the agent to ring and shows none itself: the alerts it
    // keeps ringing, and the settle_alert the agent tells it with when one ended elsewhere. It rides
    // on the server's inbox, so it comes after AddChannelServer.
    public static IMcpServerBuilder AddChannelAlerts(this IMcpServerBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        if (builder.Services.All(descriptor => descriptor.ServiceType != typeof(ChannelNotificationEmitter)))
        {
            throw new InvalidOperationException(
                "AddChannelAlerts rings through this server's channel inbox: call AddChannelServer first.");
        }

        builder.Services
            .AddSingleton<ChannelAlerts>()
            .AddSingleton<IChannelAlerts>(sp => sp.GetRequiredService<ChannelAlerts>());
        return builder.WithTools<ChannelAlertSettleTool>();
    }
}