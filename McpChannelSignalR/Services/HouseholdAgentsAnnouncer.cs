using Domain.Contracts;
using Domain.DTOs.Household;
using Domain.DTOs.WebChat;
using Microsoft.Extensions.Hosting;

namespace McpChannelSignalR.Services;

// An owner's change to the household's agents reaches the pickers already open in that household:
// the save is heard once the configuration client has dropped what it kept, and the household's
// connections are told to ask for their agents again, as the agent's own registration tells every
// page (RegisterAgentsTool). Before this a rename showed in an open picker only after a reload.
public sealed class HouseholdAgentsAnnouncer(IHouseholdConfigSaves saves, IHubNotificationSender hub) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        saves.Saved += OnSaved;
        await saves.ListenAsync(cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        saves.Saved -= OnSaved;
        return Task.CompletedTask;
    }

    private void OnSaved(HouseholdConfigSaved saved)
    {
        if (saved.Section == HouseholdSections.Agents)
        {
            _ = hub.SendToGroupAsync(TopicAudience.Household(saved.HouseholdId), "OnAgentsUpdated", new AgentsUpdatedNotification());
        }

        // Who may see what is read off a connection's own assertion, so its page opens a fresh one; a
        // suspended household's fresh one is refused by the session check, and its page says why.
        if (saved.Section is HouseholdConfigProtocol.PeopleChanged or HouseholdConfigProtocol.SuspensionChanged)
        {
            _ = hub.SendToGroupAsync(TopicAudience.Household(saved.HouseholdId), "OnAccessChanged", new AccessChangedNotification());
        }
    }
}