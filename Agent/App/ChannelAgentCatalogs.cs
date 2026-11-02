using System.Collections.Concurrent;
using Domain.Contracts;
using Domain.DTOs;

namespace Agent.App;

// The agents the agent registers with its channels, per household: a channel the deployment dials
// serves every household and is told the home's, and a household's own channel server is told that
// household's (its household agent catalogue). Each is read from the household's configuration when
// first needed and again as often as the configuration is kept, so an agent renamed or added there
// reaches the channels through the registration they already have — a connection re-registers a
// catalog that no longer matches the one it last sent. A turn is built from its own household's agents
// whatever a list says.
//
// Not before the agent listens (`listening`, the host's ApplicationStarted): the accounts service
// checks the agent's token for its view against the keys the agent publishes, and a fetch that finds
// nobody home would leave it refusing the agent until it may fetch again. The dialled connections wait
// for the home's first read (FirstRead), and a household's connection for its household's (ReadAsync),
// so a first registration is the agents rather than an empty list; a read that failed keeps what was
// read before, and none at all is an empty catalogue the next read fills.
public sealed class ChannelAgentCatalogs(
    IHouseholdAgentSource households,
    TimeSpan interval,
    ILogger<ChannelAgentCatalogs> logger,
    CancellationToken listening = default) : BackgroundService
{
    // The home is kept under the empty key: a channel serving every household names no household.
    private const string Home = "";

    private readonly TaskCompletionSource _firstRead = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly ConcurrentDictionary<string, IReadOnlyList<AgentDefinition>> _current = new();
    private readonly ConcurrentDictionary<string, Lazy<Task>> _firstReads = new();

    // The home's, for the channels the deployment dials.
    public IReadOnlyList<AgentDefinition> Current() => Current(null);

    public IReadOnlyList<AgentDefinition> Current(string? householdId) =>
        _current.GetValueOrDefault(householdId ?? Home, []);

    public Task FirstRead => _firstRead.Task;

    // Read once for a household the first time its channel server connects, and kept fresh from then on.
    public Task ReadAsync(string? householdId, CancellationToken ct) =>
        _firstReads.GetOrAdd(householdId ?? Home, key => new Lazy<Task>(() => RefreshAsync(key, CancellationToken.None)))
            .Value.WaitAsync(ct);

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
                await RefreshAsync(Home, stoppingToken);
                _firstRead.TrySetResult();
                await Task.WhenAll(_firstReads.Keys.Where(k => k != Home).Select(k => RefreshAsync(k, stoppingToken)));
            } while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            _firstRead.TrySetCanceled(stoppingToken);
        }
    }

    private async Task RefreshAsync(string key, CancellationToken ct)
    {
        try
        {
            _current[key] = (await households.ForAsync(key == Home ? null : key, ct)).Agents;
        }
        catch (Exception ex) when (ex is HttpRequestException or HouseholdConfigRefusedException or TaskCanceledException
                                   && !ct.IsCancellationRequested)
        {
            logger.LogWarning(ex,
                "Could not read household {Household}'s agents for its channels' catalogue; keeping the last one read",
                key == Home ? "home" : key);
        }
    }
}