using Domain.Contracts;
using Domain.DTOs;

namespace Agent.App;

// The agents the agent registers with its channels: the home household's, read from its configuration
// at startup and again as often as the configuration is kept, so an agent renamed there reaches the
// channels through the registration they already have — a connection re-registers a catalog that no
// longer matches the one it last sent. The channels' catalogue is one list for every household until
// each channel asks per household (the shared Telegram bot's /agent, the chat client's spaces); a turn
// is built from its own household's agents whatever the list says.
//
// The first read happens before the channel connections start (a hosted service registered ahead of
// them, whose start awaits it), so the first registration is not an empty one. One that fails keeps
// what was read before, and none at all is an empty catalogue the next tick fills.
public sealed class HomeAgentCatalog(
    IHouseholdAgentSource households,
    TimeSpan interval,
    ILogger<HomeAgentCatalog> logger) : BackgroundService
{
    private volatile IReadOnlyList<AgentDefinition> _current = [];

    public IReadOnlyList<AgentDefinition> Current => _current;

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        await RefreshAsync(cancellationToken);
        await base.StartAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(interval);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                await RefreshAsync(stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    private async Task RefreshAsync(CancellationToken ct)
    {
        try
        {
            _current = (await households.ForAsync(null, ct)).Agents;
        }
        catch (Exception ex) when (ex is HttpRequestException or HouseholdConfigRefusedException or TaskCanceledException
                                   && !ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Could not read the home household's agents for the channels' catalogue; keeping the last one read");
        }
    }
}