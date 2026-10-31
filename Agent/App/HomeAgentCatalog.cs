using Domain.Contracts;
using Domain.DTOs;

namespace Agent.App;

// The agents the agent registers with its channels: the home household's, read from its configuration
// once the agent is listening and again as often as the configuration is kept, so an agent renamed
// there reaches the channels through the registration they already have — a connection re-registers a
// catalog that no longer matches the one it last sent. The channels' catalogue is one list for every
// household until each channel asks per household (the shared Telegram bot's /agent, the chat
// client's spaces); a turn is built from its own household's agents whatever the list says.
//
// Not before the agent listens (`listening`, the host's ApplicationStarted): the accounts service
// checks the agent's token for its view against the keys the agent publishes, and a fetch that finds
// nobody home would leave it refusing the agent until it may fetch again. The channel connections
// wait for the first read (FirstRead), so their first registration is the home's agents rather than
// an empty list; a read that failed keeps what was read before, and none at all is an empty
// catalogue the next read fills.
public sealed class HomeAgentCatalog(
    IHouseholdAgentSource households,
    TimeSpan interval,
    ILogger<HomeAgentCatalog> logger,
    CancellationToken listening = default) : BackgroundService
{
    private readonly TaskCompletionSource _firstRead = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private volatile IReadOnlyList<AgentDefinition> _current = [];

    public IReadOnlyList<AgentDefinition> Current => _current;

    public Task FirstRead => _firstRead.Task;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            if (listening.CanBeCanceled && !listening.IsCancellationRequested)
            {
                var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                await using (listening.Register(() => started.TrySetResult()))
                {
                    await started.Task.WaitAsync(stoppingToken);
                }
            }

            using var timer = new PeriodicTimer(interval);
            do
            {
                await RefreshAsync(stoppingToken);
                _firstRead.TrySetResult();
            } while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            _firstRead.TrySetCanceled(stoppingToken);
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