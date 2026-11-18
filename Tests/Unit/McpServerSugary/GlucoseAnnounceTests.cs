using System.Text.Json.Nodes;
using Domain.Contracts;
using Domain.DTOs.Channel;
using Domain.DTOs.Sugary;
using Domain.DTOs.Voice;
using Domain.Exceptions;
using Domain.Tools.Sugary.Watches;
using McpServerSugary.Glucose;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace Tests.Unit.McpServerSugary;

// A glucose watch's alarm rings on the household's satellites when it names some and they can hear
// it; otherwise — no target, every satellite offline, the voice server unreachable — on the channels
// of the person whose sensor it is and of whoever wrote the watch, as a timer's does.
public class GlucoseAnnounceTests
{
    private static readonly Account _her = new("household-home", "user-her");
    private const string Writer = "user-him";

    private readonly RecordingVoices _voices = new();
    private readonly RecordingChannels _channels = new();

    private GlucoseAnnounce Announce() => new(_voices, _channels, NullLogger<GlucoseAnnounce>.Instance);

    [Fact]
    public async Task ATarget_RingsAnAlarmOnTheSubjectsHouseholdsSatellites()
    {
        _voices.Answer = Started("bedroom-01");

        var rang = await Announce().RingAsync(
            Fired(writer: Writer), Effect("""{"room": "Bedroom"}"""), Report(), CancellationToken.None);

        rang.ShouldBeTrue();
        var (household, request) = _voices.Requests.ShouldHaveSingleItem();
        household.ShouldBe(_her.TenantId);
        request.Target.Room.ShouldBe("Bedroom");
        request.Text.ShouldBe("Glucose is low");
        request.Kind.ShouldBe(AnnounceKind.Alarm);
        _channels.Rung.ShouldBeEmpty();
    }

    // Left out, it rings as a timer does: at once, at full volume, until somebody stops it — never a
    // one-shot announcement nothing could dismiss.
    [Fact]
    public async Task NoInsistentOptions_RingsInsistentlyAsATimerDoes()
    {
        _voices.Answer = Started("bedroom-01");

        await Announce().RingAsync(Fired(), Effect("""{"all": true}"""), Report(), CancellationToken.None);

        _voices.Requests.ShouldHaveSingleItem().Request.Insistent
            .ShouldBe(new InsistentOptions { GapSeconds = 10, MaxRepeats = 12, RampStartPercent = 100 });
    }

    // Given, its fields are hers and what it leaves out is the voice server's alarm default, as a home
    // watch's announcement reads it.
    [Fact]
    public async Task InsistentOptions_AreTheWatchsOwn()
    {
        _voices.Answer = Started("bedroom-01");

        await Announce().RingAsync(
            Fired(), Effect("""{"all": true}""", """{"gapSeconds": 30, "maxDurationSeconds": 600}"""), Report(),
            CancellationToken.None);

        _voices.Requests.ShouldHaveSingleItem().Request.Insistent
            .ShouldBe(new InsistentOptions { GapSeconds = 30, MaxDurationSeconds = 600 });
    }

    [Fact]
    public async Task NoTarget_RingsOnTheSubjectsChannelsAndAsksNoVoiceServer()
    {
        var rang = await Announce().RingAsync(Fired(), Effect(target: null), Report(), CancellationToken.None);

        rang.ShouldBeTrue();
        _voices.Requests.ShouldBeEmpty();
        _channels.Rung.ShouldBe([new ChannelAlertRequest(_her, "Glucose is low", AnnounceKind.Alarm, "jonas")]);
    }

    [Fact]
    public async Task EverySatelliteOffline_RingsOnTheSubjectsAndTheWritersChannels()
    {
        _voices.Answer = new AnnounceResponse
        {
            AnnouncementId = "a1",
            Satellites =
            [
                new AnnouncementOutcome { Id = "bedroom-01", Status = AnnouncementOutcome.Offline },
                new AnnouncementOutcome { Id = "kitchen-01", Status = AnnouncementOutcome.Offline }
            ]
        };

        var rang = await Announce().RingAsync(
            Fired(writer: Writer), Effect("""{"all": true}"""), Report(), CancellationToken.None);

        rang.ShouldBeTrue();
        _channels.Rung.ShouldBe(
        [
            new ChannelAlertRequest(_her, "Glucose is low", AnnounceKind.Alarm, "jonas"),
            new ChannelAlertRequest(new Account(_her.TenantId, Writer), "Glucose is low", AnnounceKind.Alarm, "jonas")
        ], ignoreOrder: true);
    }

    [Fact]
    public async Task OneSatelliteHearingIt_IsEnough()
    {
        _voices.Answer = new AnnounceResponse
        {
            AnnouncementId = "a1",
            Satellites =
            [
                new AnnouncementOutcome { Id = "bedroom-01", Status = AnnouncementOutcome.Offline },
                new AnnouncementOutcome { Id = "kitchen-01", Status = AnnouncementOutcome.Started }
            ]
        };

        (await Announce().RingAsync(Fired(writer: Writer), Effect("""{"all": true}"""), Report(), CancellationToken.None))
            .ShouldBeTrue();

        _channels.Rung.ShouldBeEmpty();
    }

    [Fact]
    public async Task TheVoiceServerUnreachable_RingsOnChannels()
    {
        _voices.Throw = new VoiceHubUnavailableException("The voice hub is unreachable: tunnel not open");

        var rang = await Announce().RingAsync(
            Fired(writer: Writer), Effect("""{"room": "Bedroom"}"""), Report(), CancellationToken.None);

        rang.ShouldBeTrue();
        _channels.Rung.Count.ShouldBe(2);
    }

    // The writer's alert is rung whatever the subject's came to: one channel taking it is not the
    // other person told.
    [Fact]
    public async Task TheSubjectsAlertTaken_StillRingsTheWriters()
    {
        _channels.Takes = alert => alert.Owner.UserId == _her.UserId;

        (await Announce().RingAsync(Fired(writer: Writer), Effect(target: null), Report(), CancellationToken.None))
            .ShouldBeTrue();

        _channels.Rung.Select(alert => alert.Owner.UserId).ShouldBe([_her.UserId, Writer], ignoreOrder: true);
    }

    [Fact]
    public async Task NoAgentTakingAnyAlert_RangNothing()
    {
        _channels.Takes = _ => false;

        (await Announce().RingAsync(Fired(writer: Writer), Effect(target: null), Report(), CancellationToken.None))
            .ShouldBeFalse();
    }

    [Fact]
    public async Task AVoiceServerAnsweringWrongly_RangNothingAndThrowsNothing()
    {
        _voices.Throw = new HttpRequestException("404: no matching satellites");

        (await Announce().RingAsync(Fired(), Effect("""{"room": "Attic"}"""), Report(), CancellationToken.None))
            .ShouldBeFalse();

        _channels.Rung.ShouldBeEmpty();
    }

    // However it came to the channels, their failing is a fire that rang nothing, never a throw.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ChannelsFailing_RangNothingAndThrowsNothing(bool hubUnreachable)
    {
        _voices.Throw = new VoiceHubUnavailableException("The voice hub is unreachable: tunnel not open");
        _channels.Takes = _ => throw new InvalidOperationException("the inbox is gone");

        (await Announce().RingAsync(
                Fired(), Effect(hubUnreachable ? """{"room": "Bedroom"}""" : null), Report(), CancellationToken.None))
            .ShouldBeFalse();
    }

    [Fact]
    public async Task ACancelledFire_IsTheAbortItIs()
    {
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        _voices.Throw = new OperationCanceledException(cancelled.Token);

        await Should.ThrowAsync<OperationCanceledException>(() =>
            Announce().RingAsync(Fired(), Effect("""{"all": true}"""), Report(), cancelled.Token));
    }

    private static AnnounceResponse Started(string satellite) => new()
    {
        AnnouncementId = "a1",
        Satellites = [new AnnouncementOutcome { Id = satellite, Status = AnnouncementOutcome.Started }]
    };

    private static GlucoseAnnounceEffect Effect(string? target, string? insistent = null) => new(
        "Glucose is low",
        target is null ? null : JsonNode.Parse(target)!.AsObject(),
        insistent is null ? null : JsonNode.Parse(insistent)!.AsObject());

    private static FiredGlucoseWatch Fired(string? writer = null)
    {
        var trigger = new GlucoseLevelTrigger(GlucoseBound.Below, 70);
        var spec = new GlucoseWatchSpec
        {
            Name = "low",
            Triggers = [trigger],
            Effects = [new GlucoseAnnounceEffect("Glucose is low", null, null)]
        };
        return new FiredGlucoseWatch(_her, new StoredGlucoseWatch
        {
            Id = "low",
            SubjectUserId = _her.UserId,
            WriterUserId = writer ?? _her.UserId,
            AgentId = "jonas",
            Spec = spec.ToJson()
        }, spec);
    }

    private static GlucoseTriggerFire Report() => new(
        0, new GlucoseLevelTrigger(GlucoseBound.Below, 70), DateTimeOffset.Parse("2026-11-18T03:10:00Z"),
        new SensorReading(DateTimeOffset.Parse("2026-11-18T03:10:00Z"), 62, GlucoseTrend.Falling),
        null, null, DateTimeOffset.Parse("2026-11-18T03:10:00Z"), null);

    private sealed class RecordingVoices : IHouseholdVoices, IInsistentAnnouncer
    {
        private readonly List<(string Household, AnnounceRequest Request)> _requests = [];
        private string? _household;

        public AnnounceResponse Answer { get; set; } = new() { AnnouncementId = "a1", Satellites = [] };
        public Exception? Throw { get; set; }

        public IReadOnlyList<(string Household, AnnounceRequest Request)> Requests
        {
            get { lock (_requests) { return _requests.ToList(); } }
        }

        public HouseholdVoice For(string tenantId)
        {
            _household = tenantId;
            return new HouseholdVoice(this, null!, null!);
        }

        public Task<AnnounceResponse> StartAsync(AnnounceRequest request, CancellationToken ct)
        {
            lock (_requests)
            {
                _requests.Add((_household!, request));
            }

            return Throw is { } thrown ? Task.FromException<AnnounceResponse>(thrown) : Task.FromResult(Answer);
        }
    }

    private sealed class RecordingChannels : IChannelAlerts
    {
        private readonly List<ChannelAlertRequest> _rung = [];

        public Func<ChannelAlertRequest, bool> Takes { get; set; } = _ => true;

        public IReadOnlyList<ChannelAlertRequest> Rung
        {
            get { lock (_rung) { return _rung.ToList(); } }
        }

        public Task<bool> RingAsync(ChannelAlertRequest alert, CancellationToken ct)
        {
            lock (_rung)
            {
                _rung.Add(alert);
            }

            return Task.FromResult(Takes(alert));
        }

        public Task<IReadOnlyList<DismissedAlert>> StopAllAsync(string tenantId, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<DismissedAlert>>([]);
    }
}