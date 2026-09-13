using System.Text.Json;
using Domain.DTOs.Channel;
using WebChat.Client.Contracts;
using WebChat.Client.Extensions;
using WebChat.Client.State.AgentSettings;
using WebChat.Client.State.Topics;

namespace WebChat.Client.State.Effects;

public sealed class AgentSettingsEffect : IDisposable
{
    private const string KeyPrefix = "agentConfigPatch:";

    private readonly IDisposable _subscription;
    private readonly IDisposable _setAgentsRegistration;
    private readonly AgentSettingsStore _store;
    private readonly Dispatcher _dispatcher;
    private readonly ILocalStorageService _localStorage;
    private readonly ILogger<AgentSettingsEffect> _logger;
    private IReadOnlyDictionary<string, AgentModelSettings> _previous;

    public AgentSettingsEffect(
        AgentSettingsStore store,
        Dispatcher dispatcher,
        ILocalStorageService localStorage,
        ILogger<AgentSettingsEffect> logger)
    {
        _store = store;
        _dispatcher = dispatcher;
        _localStorage = localStorage;
        _logger = logger;
        _previous = store.State.ByAgent;
        _subscription = store.StateObservable.Subscribe(HandleStateChange);
        _setAgentsRegistration = dispatcher.RegisterHandler<SetAgents>(
            action => ReconcileAsync(action.Agents).LogFaults(_logger, nameof(SetAgents)));
    }

    // Every catalog, not only the first. An agent this client has not seen yet takes its
    // persisted settings; one it already knows is re-sanitized against the fresh entry, which
    // now settles a stale reasoning effort and deliberately leaves the model alone — a pick the
    // catalogue stopped listing is still the person's pick, and the server is what says so.
    public async Task ReconcileAsync(IReadOnlyList<AgentCatalogEntry> agents)
    {
        foreach (var agent in agents)
        {
            var settings = _store.State.ByAgent.GetValueOrDefault(agent.Id)
                           ?? Deserialize(await _localStorage.GetAsync($"{KeyPrefix}{agent.Id}"))
                           ?? new AgentModelSettings(null, null);
            _dispatcher.Dispatch(new AgentSettingsLoaded(
                agent.Id, AgentSettingsSelectors.Sanitize(settings, agent)));
        }
    }

    private void HandleStateChange(AgentSettingsState state)
    {
        var changed = state.ByAgent
            .Where(kv => !Equals(_previous.GetValueOrDefault(kv.Key), kv.Value))
            .ToList();
        _previous = state.ByAgent;

        changed.ForEach(kv =>
            _ = _localStorage.SetAsync($"{KeyPrefix}{kv.Key}", JsonSerializer.Serialize(kv.Value)));
    }

    private static AgentModelSettings? Deserialize(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<AgentModelSettings>(json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public void Dispose()
    {
        _subscription.Dispose();
        _setAgentsRegistration.Dispose();
    }
}