using Domain.Contracts;
using Domain.DTOs.Household;
using McpChannelSignalR.Services;
using Shouldly;

namespace Tests.Unit.McpChannelSignalR;

// An owner's change to the household's agents — a rename, an agent added or removed — reaches the
// pickers already open in that household, as the agent's own registration reaches every page; another
// household's pages, and saves of other sections, are not told.
public class HouseholdAgentsAnnouncerTests
{
    [Fact]
    public async Task ASaveOfTheAgents_TellsThatHouseholdsPagesToAskAgain()
    {
        var saves = new FakeSaves();
        var hub = new RecordingHub();
        var announcer = new HouseholdAgentsAnnouncer(saves, hub);
        await announcer.StartAsync(CancellationToken.None);

        saves.Save(new HouseholdConfigSaved("casa", HouseholdSections.Agents));
        saves.Save(new HouseholdConfigSaved("casa", HouseholdSections.Settings));

        saves.Listened.ShouldBeTrue();
        hub.Sent.ShouldBe([("household:casa", "OnAgentsUpdated")]);
    }

    [Fact]
    public async Task AStoppedAnnouncer_TellsNothing()
    {
        var saves = new FakeSaves();
        var hub = new RecordingHub();
        var announcer = new HouseholdAgentsAnnouncer(saves, hub);
        await announcer.StartAsync(CancellationToken.None);
        await announcer.StopAsync(CancellationToken.None);

        saves.Save(new HouseholdConfigSaved("casa", HouseholdSections.Agents));

        hub.Sent.ShouldBeEmpty();
    }

    private sealed class FakeSaves : IHouseholdConfigSaves
    {
        public bool Listened { get; private set; }

        public event Action<HouseholdConfigSaved>? Saved;

        public Task ListenAsync(CancellationToken ct)
        {
            Listened = true;
            return Task.CompletedTask;
        }

        public void Save(HouseholdConfigSaved saved) => Saved?.Invoke(saved);
    }

    private sealed class RecordingHub : IHubNotificationSender
    {
        public List<(string Group, string Method)> Sent { get; } = [];

        public Task SendAsync(string methodName, object notification, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("an agents save is a household's, never everybody's");

        public Task SendToGroupAsync(string groupName, string methodName, object notification, CancellationToken cancellationToken = default)
        {
            Sent.Add((groupName, methodName));
            return Task.CompletedTask;
        }
    }
}