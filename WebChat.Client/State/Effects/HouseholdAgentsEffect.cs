using Domain.DTOs.Admin;
using WebChat.Client.Contracts;
using WebChat.Client.Extensions;
using WebChat.Client.Models;
using WebChat.Client.State.Admin;

namespace WebChat.Client.State.Effects;

// The admin area's agents page asks the admin API through here: the household's agents loaded when
// the page opens, an agent's earlier versions when the owner asks for them, and each change — a save,
// an addition, a removal, a restore — answered with the agents as they are now saved. A page signed
// out since it loaded is sent to sign in and back, as the chat is.
public sealed class HouseholdAgentsEffect : IDisposable
{
    private readonly Dispatcher _dispatcher;
    private readonly IAdminService _admin;
    private readonly IAccountService _accounts;
    private readonly IReadOnlyList<IDisposable> _registrations;

    public HouseholdAgentsEffect(
        Dispatcher dispatcher, IAdminService admin, IAccountService accounts, ILogger<HouseholdAgentsEffect> logger)
    {
        _dispatcher = dispatcher;
        _admin = admin;
        _accounts = accounts;

        _registrations =
        [
            dispatcher.RegisterHandler<LoadHouseholdAgents>(_ =>
                LoadAsync().LogFaults(logger, nameof(LoadHouseholdAgents))),
            dispatcher.RegisterHandler<SaveHouseholdAgent>(action =>
                ChangeAsync(_admin.SaveAgentAsync(action.AgentId, action.Edit)).LogFaults(logger, nameof(SaveHouseholdAgent))),
            dispatcher.RegisterHandler<AddHouseholdAgent>(action =>
                ChangeAsync(_admin.AddAgentAsync(action.Addition)).LogFaults(logger, nameof(AddHouseholdAgent))),
            dispatcher.RegisterHandler<RemoveHouseholdAgent>(action =>
                ChangeAsync(_admin.RemoveAgentAsync(action.AgentId)).LogFaults(logger, nameof(RemoveHouseholdAgent))),
            dispatcher.RegisterHandler<RestoreHouseholdAgentVersion>(action =>
                ChangeAsync(_admin.RestoreAgentVersionAsync(action.AgentId, action.VersionId))
                    .LogFaults(logger, nameof(RestoreHouseholdAgentVersion))),
            dispatcher.RegisterHandler<LoadHouseholdAgentVersions>(action =>
                VersionsAsync(action.AgentId).LogFaults(logger, nameof(LoadHouseholdAgentVersions)))
        ];
    }

    public async Task LoadAsync() =>
        Settle(await _admin.GetAgentsAsync(), answered => new HouseholdAgentsLoaded(answered.Value));

    public async Task VersionsAsync(string agentId) =>
        Settle(await _admin.GetAgentVersionsAsync(agentId), answered => new HouseholdAgentVersionsLoaded(agentId, answered.Value));

    private async Task ChangeAsync(Task<AdminAnswer<AdminAgents>> change) =>
        Settle(await change, answered => new HouseholdAgentsSaved(answered.Value, answered.Warning));

    private void Settle<T>(AdminAnswer<T> answer, Func<AdminAnswer<T>.Answered, IAction> outcome) =>
        _dispatcher.Settle(_accounts, answer, outcome, notice => new HouseholdAgentsNotSaved(notice));

    public void Dispose()
    {
        foreach (var registration in _registrations)
        {
            registration.Dispose();
        }
    }
}