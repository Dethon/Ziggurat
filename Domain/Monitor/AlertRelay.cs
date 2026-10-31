using System.Collections.Concurrent;
using Domain.Contracts;
using Domain.DTOs.Channel;
using Microsoft.Extensions.Logging;

namespace Domain.Monitor;

// An insistent alert for one person — a timer with no satellite to ring on — rung wherever that
// person is. A server raises it (the timers server has no channel of its own and holds no
// credential), and the agent, the one party holding every channel, offers it on each other channel
// that can show one: the person's chat clients with their push, their Telegram private chat. The
// first dismissal ends it everywhere, as an approval's first answer does (ApprovalRelay): every
// other channel, the one that raised it included, is settled and its offer called off.
//
// It ends three other ways: the server that raised it stops it (./dismiss), and every channel
// still showing it stops; nothing could show it, and the server is told at once rather than holding
// it as ringing; or nobody dismissed it for as long as a satellite rings a timer, and it rings out
// everywhere. No turn is run and no model is asked.
public sealed class AlertRelay(IReadOnlyList<IAlertChannel> channels, ILogger<AlertRelay> logger, TimeProvider time)
{
    // As long as a satellite rings a timer — twelve rounds ten seconds apart — so a timer rings for
    // the same time wherever it rings, and an offer held open on a channel is never held for good.
    public static readonly TimeSpan RingWindow = TimeSpan.FromMinutes(2);

    // The alerts ringing now, by id, each with what its origin stopping it cancels.
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _ringing = new();

    public Task RunAsync(CancellationToken ct) =>
        Task.WhenAll(channels.Select(origin => ListenAsync(origin, ct)));

    private async Task ListenAsync(IAlertChannel origin, CancellationToken ct)
    {
        try
        {
            await foreach (var signal in origin.Alerts.WithCancellation(ct))
            {
                switch (signal)
                {
                    case AlertSignal.Ring ring:
                        _ = RingAsync(origin, ring.Alert, ct);
                        break;
                    case AlertSignal.Stop stop when _ringing.TryGetValue(stop.AlertId, out var stopped):
                        await stopped.CancelAsync();
                        break;
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
    }

    private async Task RingAsync(IAlertChannel origin, ChannelAlertNotification alert, CancellationToken ct)
    {
        using var stopped = new CancellationTokenSource();
        if (!_ringing.TryAdd(alert.AlertId, stopped))
        {
            return;
        }

        try
        {
            using var offering = CancellationTokenSource.CreateLinkedTokenSource(ct, stopped.Token);
            var dismissedOn = await FirstDismissalAsync(origin, alert, offering.Token);
            if (ct.IsCancellationRequested)
            {
                return;
            }

            var stoppedAtOrigin = stopped.IsCancellationRequested;
            // Settled before being called off, so a channel still showing it says it was dismissed
            // or rang out rather than only taking it down. The origin already knows when it stopped it.
            await Task.WhenAll(channels
                .Where(channel => channel != dismissedOn && !(stoppedAtOrigin && channel == origin))
                .Select(channel => SettleAsync(channel, alert.AlertId, dismissed: dismissedOn is not null || stoppedAtOrigin)));
            await offering.CancelAsync();
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Alert {AlertId} could not be rung", alert.AlertId);
        }
        finally
        {
            _ringing.TryRemove(alert.AlertId, out _);
        }
    }

    // The channel the person dismissed it on, or null when it ended any other way: stopped at its
    // origin, rung out, shown nowhere, or the agent stopping.
    private async Task<IAlertChannel?> FirstDismissalAsync(
        IAlertChannel origin, ChannelAlertNotification alert, CancellationToken offering)
    {
        var waiting = channels
            .Where(channel => channel != origin)
            .Select(channel => (Channel: channel, Task: channel.OfferAlertAsync(alert, offering)))
            .ToList();
        var window = Task.Delay(RingWindow, time, offering);

        while (waiting.Count > 0)
        {
            var done = await Task.WhenAny(waiting.Select(w => w.Task).Append(window));
            if (done == window)
            {
                return null;
            }

            var (channel, task) = waiting.Single(w => w.Task == done);
            waiting.RemoveAll(w => w.Task == done);
            if (DismissedOn(channel, task))
            {
                return channel;
            }
        }

        logger.LogInformation("Alert {AlertId} could be shown on no channel of its person's", alert.AlertId);
        return null;
    }

    // A channel that failed, or was called off, has not dismissed it; what it failed with is logged
    // and nothing more, since the alert may still be dismissed somewhere else.
    private bool DismissedOn(IAlertChannel channel, Task<bool> task)
    {
        if (task.Exception is { } failure)
        {
            logger.LogWarning(failure.InnerException ?? failure,
                "Offering an alert on {ChannelId} failed; it is left to the other channels", channel.ChannelId);
        }

        return task.IsCompletedSuccessfully && task.Result;
    }

    private async Task SettleAsync(IAlertChannel channel, string alertId, bool dismissed)
    {
        try
        {
            await channel.SettleAlertAsync(alertId, dismissed, CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Telling {ChannelId} that alert {AlertId} ended failed", channel.ChannelId, alertId);
        }
    }
}