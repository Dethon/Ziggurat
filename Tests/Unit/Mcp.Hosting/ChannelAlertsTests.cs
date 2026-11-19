using Domain.Channels;
using Domain.DTOs.Channel;
using Domain.DTOs.Voice;
using global::Mcp.Hosting;
using Shouldly;

namespace Tests.Unit.Mcp.Hosting;

// Any server that holds no channel rings a person's alert the same way: into its own inbox for the
// agent, which offers it wherever the person is. What the server keeps is which of its alerts still
// ring, by household, so a stop there finds them and a settle from the agent forgets one.
public class ChannelAlertsTests
{
    private const string Agent = ChannelProtocol.ChannelClientNamePrefix + "sugary";

    private static readonly Account _her = new("0a1b", "her");
    private static readonly Account _neighbour = new("0c2d", "n1");

    private readonly ChannelInbox _inbox = new();
    private readonly ChannelAlerts _alerts;

    public ChannelAlertsTests()
    {
        _alerts = new ChannelAlerts(new ChannelNotificationEmitter(_inbox, DeliveryPolicy.GateOnLive));
    }

    [Fact]
    public async Task ARing_IsRaisedForTheAgent_AsTheRequestSaysIt()
    {
        await AgentPollsAsync();

        (await _alerts.RingAsync(new ChannelAlertRequest(_her, "Glucose is low", AnnounceKind.Alarm, "jonas"),
            CancellationToken.None)).ShouldBeTrue();

        var alert = (await AgentPollsAsync()).ShouldHaveSingleItem().Alert.ShouldNotBeNull();
        alert.Owner.ShouldBe(_her);
        alert.Text.ShouldBe("Glucose is low");
        alert.Kind.ShouldBe(AnnounceKind.Alarm);
        alert.AgentId.ShouldBe("jonas");
        alert.AlertId.Length.ShouldBe(32);
    }

    // Gate-on-live: an alert no agent was there to take rings nowhere, and nothing holds it as ringing.
    [Fact]
    public async Task ARingNoAgentTook_IsNotRung_AndNothingIsLeftToStop()
    {
        (await _alerts.RingAsync(new ChannelAlertRequest(_her, "Glucose is low", AnnounceKind.Alarm, null),
            CancellationToken.None)).ShouldBeFalse();

        (await _alerts.StopAllAsync(_her.TenantId, CancellationToken.None)).ShouldBeEmpty();
    }

    [Fact]
    public async Task AStop_StopsTheHouseholdsAlertsAlone_AndTellsTheAgent()
    {
        await AgentPollsAsync();
        await _alerts.RingAsync(new ChannelAlertRequest(_her, "Glucose is low", AnnounceKind.Alarm, null), CancellationToken.None);
        await _alerts.RingAsync(new ChannelAlertRequest(_neighbour, "tea timer", AnnounceKind.Timer, null), CancellationToken.None);
        var hers = (await AgentPollsAsync()).Select(item => item.Alert!).Single(alert => alert.Owner == _her);

        (await _alerts.StopAllAsync(_her.TenantId, CancellationToken.None))
            .ShouldBe([new DismissedAlert("Glucose is low", AnnounceKind.Alarm)]);

        (await AgentPollsAsync()).ShouldHaveSingleItem().AlertStopped.ShouldNotBeNull().AlertId.ShouldBe(hers.AlertId);
        (await _alerts.StopAllAsync(_neighbour.TenantId, CancellationToken.None))
            .ShouldBe([new DismissedAlert("tea timer", AnnounceKind.Timer)]);
    }

    // The agent's word that it ended on a channel: a later stop has nothing of it to stop.
    [Fact]
    public async Task ASettledAlert_IsNoLongerStoppedHere()
    {
        await AgentPollsAsync();
        await _alerts.RingAsync(new ChannelAlertRequest(_her, "Glucose is low", AnnounceKind.Alarm, null), CancellationToken.None);
        var alert = (await AgentPollsAsync()).ShouldHaveSingleItem().Alert!;

        _alerts.Settle(alert.AlertId);

        (await _alerts.StopAllAsync(_her.TenantId, CancellationToken.None)).ShouldBeEmpty();
    }

    // The agent's connection polling the server: the first poll is what makes it live.
    private Task<IReadOnlyList<ChannelInboxItem>> AgentPollsAsync() =>
        _inbox.ReceiveAsync(Agent, TimeSpan.Zero, CancellationToken.None);
}