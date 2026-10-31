using Domain.Monitor;

namespace Agent.App;

// Runs the agent's one alert relay for the life of the process: what any channel's server raises —
// a timer with no satellite to ring on — is rung on its person's other channels. The relay keeps no
// state that a fault would need to recover, and reading a channel's alerts never ends while the
// process runs, so this only starts it and lets shutdown stop it.
public sealed class AlertRinging(AlertRelay relay) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken) => relay.RunAsync(stoppingToken);
}