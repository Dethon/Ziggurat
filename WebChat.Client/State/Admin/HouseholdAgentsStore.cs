using Domain.DTOs.Admin;

namespace WebChat.Client.State.Admin;

public record LoadHouseholdAgents : IAction;

public record HouseholdAgentsLoaded(AdminAgents Agents) : IAction;

public record SaveHouseholdAgent(string AgentId, AgentEdit Edit) : IAction;

public record AddHouseholdAgent(AgentAddition Addition) : IAction;

public record RemoveHouseholdAgent(string AgentId) : IAction;

// Which agent answers the household's satellites; null marks none.
public record SetHouseholdVoiceAgent(string? AgentId) : IAction;

public record RestoreHouseholdAgentVersion(string AgentId, string VersionId) : IAction;

// A change the admin API answered with the household's agents as they are now saved.
public record HouseholdAgentsSaved(AdminAgents Agents, string? Warning) : IAction;

public record LoadHouseholdAgentVersions(string AgentId) : IAction;

public record HouseholdAgentVersionsLoaded(string AgentId, IReadOnlyList<AdminAgentVersion> Versions) : IAction;

// A load or a change the admin API did not answer with the agents: refused, not allowed, unreachable.
public record HouseholdAgentsNotSaved(AdminNotice Notice) : IAction;

public sealed record HouseholdAgentsState
{
    // Null until the admin API has answered with them.
    public AdminAgents? Agents { get; init; }

    // The agent whose earlier versions are shown, and those versions once the API answered them.
    public string? VersionsOf { get; init; }

    public IReadOnlyList<AdminAgentVersion>? Versions { get; init; }

    public bool Saving { get; init; }

    public AdminNotice? Notice { get; init; }

    public static HouseholdAgentsState Initial => new();
}

// The household's agents as the admin area's agents page shows them. What is shown is only ever what
// the admin API answered: a change shows as saved once the API says so, and every change answers the
// whole list, so what the page shows is what is saved.
public sealed class HouseholdAgentsStore : IDisposable
{
    private readonly Store<HouseholdAgentsState> _store;

    public HouseholdAgentsStore(Dispatcher dispatcher)
    {
        _store = new Store<HouseholdAgentsState>(HouseholdAgentsState.Initial);

        dispatcher.RegisterCatchAll(action => _store.Dispatch(action, Reduce));
    }

    public HouseholdAgentsState State => _store.State;

    public IObservable<HouseholdAgentsState> StateObservable => _store.StateObservable;

    public void Dispose() => _store.Dispose();

    private static HouseholdAgentsState Reduce(HouseholdAgentsState state, IAction action) => action switch
    {
        LoadHouseholdAgents => state with { Notice = null },
        HouseholdAgentsLoaded a => state with { Agents = a.Agents },
        SaveHouseholdAgent or AddHouseholdAgent or RemoveHouseholdAgent or RestoreHouseholdAgentVersion or SetHouseholdVoiceAgent =>
            state with { Saving = true, Notice = null },
        // A change makes the versions shown stale: what it replaced is a version now.
        HouseholdAgentsSaved a => state with
        {
            Agents = a.Agents,
            Saving = false,
            VersionsOf = null,
            Versions = null,
            Notice = a.Warning is { } warning
                ? new AdminNotice(AdminNoticeKind.Warning, warning)
                : new AdminNotice(AdminNoticeKind.Saved, "Saved.")
        },
        LoadHouseholdAgentVersions a => state with { VersionsOf = a.AgentId, Versions = null, Notice = null },
        HouseholdAgentVersionsLoaded a when a.AgentId == state.VersionsOf => state with { Versions = a.Versions },
        HouseholdAgentsNotSaved a => state with { Saving = false, Notice = a.Notice },
        _ => state
    };
}