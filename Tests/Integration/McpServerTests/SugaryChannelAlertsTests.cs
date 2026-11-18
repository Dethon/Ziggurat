using Domain.Contracts;
using Domain.DTOs.Channel;
using Domain.DTOs.Voice;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Tests.Integration.Fixtures;

namespace Tests.Integration.McpServerTests;

// A glucose alarm with no satellite to ring on rings on its person's channels as a timer's does:
// Sugary raises it in its own channel's inbox, and the agent's connection to Sugary reads it apart
// from turns, as an alert to ring wherever the person is. Sugary answers settle_alert, so an alert
// that ended on a channel is no longer Sugary's to stop.
public class SugaryChannelAlertsTests
{
    [Fact]
    public async Task ARingRaisedThroughSugary_ReachesTheAgentAsAnAlert()
    {
        await using var server = await SugaryServer.StartAsync();
        var agent = await server.AgentAsync();

        (await Alerts(server).RingAsync(
                new ChannelAlertRequest(SugaryServer.Her, "Glucose is low", AnnounceKind.Alarm, "jonas"), CancellationToken.None))
            .ShouldBeTrue();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var alert = (await agent.Alerts.FirstAsync(cts.Token)).ShouldBeOfType<AlertSignal.Ring>().Alert;
        alert.Owner.ShouldBe(SugaryServer.Her);
        alert.Text.ShouldBe("Glucose is low");
        alert.Kind.ShouldBe(AnnounceKind.Alarm);
        alert.AgentId.ShouldBe("jonas");
    }

    [Fact]
    public async Task AnAlertTheAgentSettled_IsNoLongerSugarysToStop()
    {
        await using var server = await SugaryServer.StartAsync();
        var agent = await server.AgentAsync();
        var alerts = Alerts(server);
        await alerts.RingAsync(new ChannelAlertRequest(SugaryServer.Her, "Glucose is low", AnnounceKind.Alarm, null), CancellationToken.None);
        await alerts.RingAsync(new ChannelAlertRequest(SugaryServer.Him, "Glucose is high", AnnounceKind.Alarm, null), CancellationToken.None);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var rung = await agent.Alerts.Take(2).Select(signal => ((AlertSignal.Ring)signal).Alert).ToListAsync(cts.Token);

        await agent.SettleAlertAsync(rung.Single(alert => alert.Text == "Glucose is low").AlertId, dismissed: true, CancellationToken.None);

        (await alerts.StopAllAsync(SugaryServer.Her.TenantId, CancellationToken.None))
            .ShouldBe([new DismissedAlert("Glucose is high", AnnounceKind.Alarm)]);
    }

    private static IChannelAlerts Alerts(SugaryServer server) => server.Services.GetRequiredService<IChannelAlerts>();
}