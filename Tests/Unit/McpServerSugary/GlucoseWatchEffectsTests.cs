using Domain.Channels;
using Domain.DTOs;
using Domain.DTOs.Channel;
using Domain.DTOs.Sugary;
using Domain.Tools.Sugary.Watches;
using Mcp.Hosting;
using McpServerSugary.Glucose;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Shouldly;

namespace Tests.Unit.McpServerSugary;

// A glucose watch's effects as its fire runs them, in the order written: each prompt composed as an
// unprompted fire and put on Sugary's channel for whichever agent is listening, each announcement handed
// to the announce seam. A real inbox behind the real emitter, drained as the agent's connection drains it.
public class GlucoseWatchEffectsTests
{
    private const string Subscriber = ChannelProtocol.ChannelClientNamePrefix + "sugary";
    private static readonly DateTimeOffset _now = new(2026, 11, 18, 21, 14, 0, TimeSpan.Zero);
    private static readonly Account _her = new("household-home", "user-her");

    private sealed class RecordingAnnounce : IGlucoseAnnounceEffect
    {
        public bool Rings { get; set; } = true;
        public bool Throws { get; set; }
        public List<(string Watch, GlucoseAnnounceEffect Effect, GlucoseTriggerFire Report)> Rung { get; } = [];

        public Task<bool> RingAsync(FiredGlucoseWatch watch, GlucoseAnnounceEffect effect, GlucoseTriggerFire report, CancellationToken ct)
        {
            Rung.Add((watch.Watch.Id, effect, report));
            return Throws ? throw new InvalidOperationException("the hub fell over") : Task.FromResult(Rings);
        }
    }

    private sealed record Harness(GlucoseWatchEffects Effects, ChannelInbox Inbox, RecordingAnnounce Announce)
    {
        public async Task<IReadOnlyList<ChannelMessageNotification>> DrainAsync() =>
            (await Inbox.ReceiveAsync(Subscriber, TimeSpan.Zero, CancellationToken.None))
                .Where(i => i.Kind == ChannelInboxItemKind.Message).Select(i => i.Message!).ToList();
    }

    private static async Task<Harness> BuildAsync(bool listening = true)
    {
        var clock = new FakeTimeProvider(_now);
        var inbox = new ChannelInbox(clock);
        if (listening)
        {
            await inbox.ReceiveAsync(Subscriber, TimeSpan.Zero, CancellationToken.None);
        }

        var announce = new RecordingAnnounce();
        var effects = new GlucoseWatchEffects(
            new ChannelNotificationEmitter(inbox, DeliveryPolicy.GateOnLive), announce,
            new DeliverySettings { DefaultDeliverTo = ["signalr"] }, clock, NullLogger<GlucoseWatchEffects>.Instance);
        return new Harness(effects, inbox, announce);
    }

    private const string Effects = """
        [{"kind":"prompt","prompt":"Tell her she is low"},{"kind":"announce","text":"Glucose is low"},
         {"kind":"prompt","prompt":"Log it in her notes"}]
        """;

    private static FiredGlucoseWatch Watch(string effects = Effects, string? delegation = null, string? userId = "user-her")
    {
        var spec = GlucoseWatchSpec.Parse(
            $$"""{"name":"Low","triggers":[{"trigger":"glucose","below":70}],"effects":{{effects}},"deliverTo":["telegram"],"userId":"{{userId}}"}""");
        return new FiredGlucoseWatch(_her, new StoredGlucoseWatch
        {
            Id = "low",
            SubjectUserId = _her.UserId,
            WriterUserId = _her.UserId,
            AgentId = "jonas",
            Spec = spec.ToJson(),
            DeliverTo = spec.DeliverTo,
            UserId = userId,
            Delegation = delegation
        }, spec)
        { Zone = TimeZoneInfo.Utc, Unit = GlucoseUnit.Mmol };
    }

    private static readonly GlucoseTriggerFire _report = new(0, new GlucoseLevelTrigger(GlucoseBound.Below, 70), _now,
        new SensorReading(_now, 65, GlucoseTrend.Falling), null, null, _now, null);

    [Fact]
    public async Task EveryEffect_RunsInOrder_AndWhatReachedSomebodyIsCounted()
    {
        var harness = await BuildAsync();

        var outcome = await harness.Effects.FireAsync(Watch(), _report, CancellationToken.None);

        outcome.Delivered.ShouldBe(3);
        var fires = await harness.DrainAsync();
        fires.Select(f => f.Content).ShouldBe(["Tell her she is low", "Log it in her notes"]);
        fires.Select(f => f.ConversationId).Distinct().Count().ShouldBe(2, "two prompts of one fire are two conversations");
        harness.Announce.Rung.ShouldHaveSingleItem().Effect.Text.ShouldBe("Glucose is low");
    }

    // The prompt is the watch's own words, which a delegation covers; everything Sugary saw at fire time
    // rides beside it as the report, and the rest is a fire as the scheduler composes one.
    [Fact]
    public async Task APrompt_IsComposedAsAnUnpromptedFire_WithTheReportBesideIt()
    {
        var harness = await BuildAsync();
        var watch = Watch("""[{"kind":"prompt","prompt":"Tell her she is low"}]""", delegation: "the-token");

        await harness.Effects.FireAsync(watch, _report, CancellationToken.None);

        var fire = (await harness.DrainAsync()).ShouldHaveSingleItem();
        fire.Content.ShouldBe("Tell her she is low");
        fire.FireReport.ShouldNotBeNull().ShouldContain("65 mg/dL (3.6 mmol/L)");
        fire.Sender.ShouldBe(ServiceSenders.GlucoseWatch);
        fire.AgentId.ShouldBe("jonas");
        fire.Origin.ShouldBe(new MessageOrigin(MessageOriginKind.Watch, null, WatchId: "low", Title: "Low"));
        fire.ReplyTo.ShouldBe([new ReplyTarget("telegram", null)]);
        fire.UserId.ShouldBe("user-her");
        fire.Principal.ShouldBe(new Principal("sugary", "user-her", _her));
        fire.HouseholdId.ShouldBe(_her.TenantId);
        fire.Delegation.ShouldBe(new FireDelegation("the-token", GlucoseWatchDelegation.Of(watch.Watch).Description));
        fire.Timestamp.ShouldBe(_now);
    }

    [Fact]
    public async Task AWatchWithNoDelegation_FiresCarryingNone()
    {
        var harness = await BuildAsync();

        await harness.Effects.FireAsync(Watch("""[{"kind":"prompt","prompt":"Tell her"}]"""), _report, CancellationToken.None);

        (await harness.DrainAsync()).ShouldHaveSingleItem().Delegation.ShouldBeNull();
    }

    // Gate-on-live: a prompt no agent is polling for is not kept for later, and reached nobody.
    [Fact]
    public async Task APromptNoAgentTook_ReachedNobody()
    {
        var harness = await BuildAsync(listening: false);
        harness.Announce.Rings = false;

        (await harness.Effects.FireAsync(Watch(), _report, CancellationToken.None)).Delivered.ShouldBe(0);
    }

    [Fact]
    public async Task AnAnnouncementThatFails_IsContained_AndTheRestStillRun()
    {
        var harness = await BuildAsync();
        harness.Announce.Throws = true;

        (await harness.Effects.FireAsync(Watch(), _report, CancellationToken.None)).Delivered.ShouldBe(2);
        (await harness.DrainAsync()).Count.ShouldBe(2);
    }
}