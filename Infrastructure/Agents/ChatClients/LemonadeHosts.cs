using System.Collections.Concurrent;
using Domain.Contracts;
using Domain.DTOs.Channel;
using Domain.DTOs.Household;
using Infrastructure.Clients;
using Microsoft.Extensions.Logging;

namespace Infrastructure.Agents.ChatClients;

// A Lemonade chat host and the models it offers now.
public sealed record LemonadeHost(LemonadeChatHostOptions Options, ILemonadeModelSource Models);

// Which Lemonade box a household's turns may run on, and the models each household is offered. A box
// is a household's own (its keys section, set on its Keys page); the home with none of its own runs on
// the deployment's configured box; any other household with none runs on none, and is offered none.
public interface ILemonadeHosts
{
    LemonadeHost? For(string? householdId, bool home);

    // Each household's own box's models, by household — the home's under its id once it is known, its
    // own box's or else the deployment's — for the chat client's picker.
    IReadOnlyDictionary<string, IReadOnlyList<LemonadeModel>> ModelsByHousehold { get; }
}

// The deployment's box alone, for the home: a host that reads no household's boxes (a test, the eval).
public sealed class DeploymentLemonadeHost(LemonadeChatHostOptions? options, ILemonadeModelSource models) : ILemonadeHosts
{
    public LemonadeHost? For(string? householdId, bool home) =>
        home && options is { IsConfigured: true } ? new LemonadeHost(options, models) : null;

    public IReadOnlyDictionary<string, IReadOnlyList<LemonadeModel>> ModelsByHousehold { get; } =
        new Dictionary<string, IReadOnlyList<LemonadeModel>>();
}

// Every household's own box, read from the agent's lemonade-boxes view on each refresh, each with a
// discovery of its own; the deployment's box beside them for the home. A box whose address or key
// changed gets a fresh discovery; one removed is dropped at once.
public sealed class HouseholdLemonadeHosts(
    LemonadeChatHostOptions deployment,
    ILemonadeModelSource deploymentModels,
    IHouseholdConfiguration households,
    ILoggerFactory loggers) : ILemonadeHosts
{
    private readonly ILogger _logger = loggers.CreateLogger<HouseholdLemonadeHosts>();
    private readonly ConcurrentDictionary<string, (LemonadeBox Box, LemonadeHost Host, LemonadeModelDiscovery Discovery)> _boxes = new();
    private volatile string? _home;

    public LemonadeHost? For(string? householdId, bool home)
    {
        if (home && householdId is not null)
        {
            _home = householdId;
        }

        return householdId is not null && _boxes.TryGetValue(householdId, out var own)
            ? own.Host
            : home && deployment.IsConfigured
                ? new LemonadeHost(deployment, deploymentModels)
                : null;
    }

    public IReadOnlyDictionary<string, IReadOnlyList<LemonadeModel>> ModelsByHousehold
    {
        get
        {
            var models = _boxes.ToDictionary(b => b.Key, b => b.Value.Host.Models.Current);
            if (_home is { } home && !models.ContainsKey(home) && deployment.IsConfigured)
            {
                models[home] = deploymentModels.Current;
            }

            return models;
        }
    }

    public async Task RefreshAsync(CancellationToken ct)
    {
        try
        {
            var boxes = (await households.LemonadeBoxesAsync(ct)).ToDictionary(b => b.HouseholdId, b => b.Box);
            foreach (var gone in _boxes.Keys.Where(id => !boxes.ContainsKey(id)).ToList())
            {
                _boxes.TryRemove(gone, out _);
            }

            foreach (var (household, box) in boxes.Where(b => !_boxes.TryGetValue(b.Key, out var kept) || kept.Box != b.Value))
            {
                var options = new LemonadeChatHostOptions { ApiUrl = box.ApiUrl, ApiKey = box.ApiKey };
                var discovery = new LemonadeModelDiscovery(
                    new HttpClient(HostedConnectionPool.Shared, disposeHandler: false), options,
                    loggers.CreateLogger<LemonadeModelDiscovery>());
                _boxes[household] = (box, new LemonadeHost(options, discovery), discovery);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            // The boxes known stay as they were: an accounts outage is not every box removed.
            _logger.LogWarning(ex, "The households' Lemonade boxes could not be read; keeping the ones known");
        }

        await Task.WhenAll(_boxes.Values.Select(b => b.Discovery.RefreshAsync(ct)));
    }
}