using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Domain.Contracts;
using Domain.DTOs.Channel;
using Domain.DTOs.Sugary;
using Domain.DTOs.Voice;
using Domain.Exceptions;
using Domain.Monitor;
using Domain.Security;
using Domain.Tools.Sugary.Watches;
using Infrastructure.Clients.Channels;
using Infrastructure.Timers;
using Mcp.Hosting;
using McpServerSugary.Glucose;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Tests.Integration.Fixtures;
using Tests.Integration.McpServers;
using Tests.Integration.McpServerTimers;

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

    // A watch's alarm whose satellites are all off is heard nowhere there, so it rings on her chat
    // client and Telegram through the agent's relay, as a timer's does; her dismissal on one channel
    // ends it on the other and at Sugary, which then holds nothing of it as ringing.
    [Fact]
    public async Task AWatchsAlarmWhoseSatellitesAreAllOffline_IsOfferedOnHerChannels_AndHerDismissalSettlesIt()
    {
        await using var ring = await AlertRing.StartAsync(new OfflineVoices());

        (await ring.AnnounceAsync(writer: SugaryServer.Her, target: new JsonObject { ["all"] = true })).ShouldBeTrue();

        await Eventually.Until(() => !ring.WebChat.Offers.IsEmpty && !ring.Telegram.Offers.IsEmpty, "the alarm to be offered on both channels");
        var offer = ring.WebChat.Offers.ShouldHaveSingleItem();
        offer.Owner.ShouldBe(SugaryServer.Her);
        offer.Text.ShouldBe("Glucose is low");
        offer.Kind.ShouldBe(AnnounceKind.Alarm);
        offer.AgentId.ShouldBe("jonas");
        ring.Telegram.Offers.ShouldHaveSingleItem().AlertId.ShouldBe(offer.AlertId);
        ring.WebChat.Dismiss(offer.AlertId);

        await Eventually.Until(
            () => ring.Telegram.CalledOff.Contains(offer.AlertId) && ring.SugarySettled.Contains(offer.AlertId),
            "the Telegram offer to be called off and Sugary told");
        ring.Telegram.Settles.ShouldHaveSingleItem().ShouldBe((offer.AlertId, true));
        (await Alerts(ring.Server).StopAllAsync(SugaryServer.Her.TenantId, CancellationToken.None)).ShouldBeEmpty();
    }

    // With no target, or the household's voice server unreachable, it rings on the channels of both
    // people: the one whose sensor it watches and the one who wrote the watch.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AWatchsAlarmWithNoSatelliteToRingOn_IsOfferedToItsSubjectAndItsWriter(bool targetted)
    {
        await using var ring = await AlertRing.StartAsync(new UnreachableVoices());

        (await ring.AnnounceAsync(writer: SugaryServer.Him, target: targetted ? new JsonObject { ["room"] = "Bedroom" } : null))
            .ShouldBeTrue();

        await Eventually.Until(() => ring.WebChat.Offers.Count == 2, "an offer for each of them");
        ring.WebChat.Offers.Select(offer => offer.Owner).ShouldBe([SugaryServer.Her, SugaryServer.Him], ignoreOrder: true);
    }

    private static IChannelAlerts Alerts(SugaryServer server) => server.Services.GetRequiredService<IChannelAlerts>();

    // Sugary with its household's voice server standing in, the agent's connection to it, two channels
    // that show alerts (her chat client and Telegram), and the agent's relay over the three.
    private sealed class AlertRing : IAsyncDisposable
    {
        private readonly List<IAsyncDisposable> _owned = [];
        private readonly CancellationTokenSource _stopping = new();

        private AlertRing(SugaryServer server) => Server = server;

        public SugaryServer Server { get; }
        public ConcurrentQueue<string> SugarySettled { get; } = new();
        public AlertProbe WebChat { get; private set; } = null!;
        public AlertProbe Telegram { get; private set; } = null!;

        public static async Task<AlertRing> StartAsync(IHouseholdVoices voices)
        {
            var ring = new AlertRing(await SugaryServer.StartAsync(services: collection =>
                collection.Replace(ServiceDescriptor.Singleton(voices))));
            var sugary = await ring.Server.AgentAsync();
            (ring.WebChat, var webchat) = await ring.ChannelAsync();
            (ring.Telegram, var telegram) = await ring.ChannelAsync();
            IAlertChannel[] connections =
            [
                new SettleWatching(sugary, ring.SugarySettled),
                await ring.ConnectAsync("signalr", webchat.Endpoint),
                await ring.ConnectAsync("telegram", telegram.Endpoint)
            ];
            _ = new AlertRelay(ConnectionSet.Of(connections), new RingingAlerts(), NullLogger<AlertRelay>.Instance, TimeProvider.System)
                .RunAsync(ring._stopping.Token);
            return ring;
        }

        public Task<bool> AnnounceAsync(Account writer, JsonObject? target)
        {
            var effect = new GlucoseAnnounceEffect("Glucose is low", target, null);
            var spec = new GlucoseWatchSpec
            {
                Name = "low",
                Triggers = [new GlucoseLevelTrigger(GlucoseBound.Below, 70)],
                Effects = [effect]
            };
            var watch = new FiredGlucoseWatch(SugaryServer.Her, new StoredGlucoseWatch
            {
                Id = "low",
                SubjectUserId = SugaryServer.Her.UserId,
                WriterUserId = writer.UserId,
                AgentId = "jonas",
                Spec = spec.ToJson()
            }, spec);
            var report = new GlucoseTriggerFire(
                0, spec.Triggers[0], DateTimeOffset.UtcNow, new SensorReading(DateTimeOffset.UtcNow, 62, GlucoseTrend.Falling),
                null, null, DateTimeOffset.UtcNow, null);
            return Server.Services.GetRequiredService<IGlucoseAnnounceEffect>().RingAsync(watch, effect, report, CancellationToken.None);
        }

        public async ValueTask DisposeAsync()
        {
            await _stopping.CancelAsync();
            foreach (var owned in Enumerable.Reverse(_owned))
            {
                await owned.DisposeAsync();
            }

            await Server.DisposeAsync();
            _stopping.Dispose();
        }

        private async Task<(AlertProbe, RunningServer)> ChannelAsync()
        {
            var probe = new AlertProbe();
            var channel = await InMemoryMcpServer.StartAsync(services => services
                .AddSingleton(probe)
                .AddToolServer(new ChannelSettings())
                .AddChannelServer(DeliveryPolicy.Broadcast, noOutboundSurface: true)
                .WithTools<AlertProbeTools>());
            _owned.Add(channel);
            return (probe, channel);
        }

        private async Task<McpChannelConnection> ConnectAsync(string channelId, string endpoint)
        {
            var connection = new McpChannelConnection(channelId, mcpSecret: McpTestSecret.Value, callTokens: McpTestSecret.CallTokens);
            await connection.ConnectAsync(endpoint, CancellationToken.None);
            _owned.Add(connection);
            return connection;
        }
    }

    // The agent's connection to Sugary as the relay sees it, noting each settle once Sugary has taken
    // it: the relay settles every channel at once, so another channel's settle says nothing of Sugary's.
    private sealed class SettleWatching(McpChannelConnection inner, ConcurrentQueue<string> settled) : IAlertChannel
    {
        public string ChannelId => inner.ChannelId;

        public IAsyncEnumerable<AlertSignal> Alerts => inner.Alerts;

        public Task<bool> OfferAlertAsync(ChannelAlertNotification alert, CancellationToken ct) => inner.OfferAlertAsync(alert, ct);

        public async Task SettleAlertAsync(string alertId, bool dismissed, CancellationToken ct)
        {
            await inner.SettleAlertAsync(alertId, dismissed, ct);
            settled.Enqueue(alertId);
        }
    }

    private sealed class OfflineVoices : IHouseholdVoices, IInsistentAnnouncer
    {
        public HouseholdVoice For(string tenantId) => new(this, null!, null!);

        public Task<AnnounceResponse> StartAsync(AnnounceRequest request, CancellationToken ct) =>
            Task.FromResult(new AnnounceResponse
            {
                AnnouncementId = "a1",
                Satellites = [new AnnouncementOutcome { Id = "bedroom-01", Status = AnnouncementOutcome.Offline }]
            });
    }

    private sealed class UnreachableVoices : IHouseholdVoices, IInsistentAnnouncer
    {
        public HouseholdVoice For(string tenantId) => new(this, null!, null!);

        public Task<AnnounceResponse> StartAsync(AnnounceRequest request, CancellationToken ct) =>
            throw new VoiceHubUnavailableException("The voice hub is unreachable: its tunnel is not open");
    }

    private sealed record ChannelSettings : IMcpHostSettings
    {
        public McpGateSettings Mcp => McpTestSecret.Gate;
    }
}