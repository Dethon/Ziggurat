using System.Collections.Concurrent;
using Domain.Contracts;

namespace Infrastructure.Timers;

// In memory, as the timers it rings are: an agent that restarts has no offer left open to stop.
public sealed class RingingAlerts : IRingingAlerts
{
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _ringing = new();

    public CancellationToken? Begin(string alertId)
    {
        var stopped = new CancellationTokenSource();
        if (_ringing.TryAdd(alertId, stopped))
        {
            return stopped.Token;
        }

        stopped.Dispose();
        return null;
    }

    public async Task StopAsync(string alertId)
    {
        if (!_ringing.TryGetValue(alertId, out var stopped))
        {
            return;
        }

        try
        {
            await stopped.CancelAsync();
        }
        catch (ObjectDisposedException)
        {
            // It ended between being found and being stopped.
        }
    }

    public void End(string alertId)
    {
        if (_ringing.TryRemove(alertId, out var stopped))
        {
            stopped.Dispose();
        }
    }
}