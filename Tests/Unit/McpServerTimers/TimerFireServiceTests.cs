using Domain.Contracts;
using Domain.DTOs.Voice;
using Domain.Exceptions;
using Infrastructure.Timers;
using McpServerTimers.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Shouldly;

namespace Tests.Unit.McpServerTimers;

public class TimerFireServiceTests
{
    private const string Home = "0a1b";

    // Every household's voice server is this one announcer: the loop's part is asking it.
    private sealed class OneVoice(IInsistentAnnouncer announcer) : IHouseholdVoices
    {
        public HouseholdVoice For(string tenantId) => new(announcer, null!, null!);
    }

    private sealed class NoChannels : IChannelAlerts
    {
        public Task<bool> RingAsync(ArmedTimer timer, CancellationToken ct) => Task.FromResult(false);

        public Task<IReadOnlyList<DismissedAlert>> StopAllAsync(string tenantId, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<DismissedAlert>>([]);
    }

    private sealed class RecordingAnnouncer : IInsistentAnnouncer
    {
        private readonly List<AnnounceRequest> _requests = [];
        public IReadOnlyList<AnnounceRequest> Requests
        {
            get { lock (_requests) { return _requests.ToList(); } }
        }
        public Task<AnnounceResponse> StartAsync(AnnounceRequest request, CancellationToken ct)
        {
            lock (_requests)
            { _requests.Add(request); }
            return Task.FromResult(new AnnounceResponse { AnnouncementId = "a1", Satellites = [] });
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.Elapsed > timeout)
            { throw new TimeoutException("condition not met"); }
            await Task.Delay(20);
        }
    }

    private sealed class RecordingChannels : IChannelAlerts
    {
        private readonly List<string> _rung = [];
        public IReadOnlyList<string> Rung
        {
            get { lock (_rung) { return _rung.ToList(); } }
        }

        public Task<bool> RingAsync(ArmedTimer timer, CancellationToken ct)
        {
            lock (_rung)
            { _rung.Add(timer.Id); }
            return Task.FromResult(true);
        }

        public Task<IReadOnlyList<DismissedAlert>> StopAllAsync(string tenantId, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<DismissedAlert>>([]);
    }

    // A voice server that answers the ring with each satellite's status as given.
    private sealed class AnsweringAnnouncer(params string[] statuses) : IInsistentAnnouncer
    {
        public int Calls;

        public Task<AnnounceResponse> StartAsync(AnnounceRequest request, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            return Task.FromResult(new AnnounceResponse
            {
                AnnouncementId = "a1",
                Satellites = [.. statuses.Select((status, i) => new AnnouncementOutcome { Id = $"sat-{i}", Status = status })]
            });
        }
    }

    // A voice server that cannot be reached at all: a household's kit whose tunnel is not open.
    private sealed class UnreachableAnnouncer : IInsistentAnnouncer
    {
        public int Calls;

        public Task<AnnounceResponse> StartAsync(AnnounceRequest request, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            throw new VoiceHubUnavailableException("The voice hub is unreachable: voice is offline: its tunnel is not open");
        }
    }

    private static Task<(RecordingChannels Channels, AnsweringAnnouncer Voice)> FireOneAsync(params string[] statuses) =>
        FireOneAsync(new AnsweringAnnouncer(statuses), voice => voice.Calls);

    private static async Task<(RecordingChannels Channels, TVoice Voice)> FireOneAsync<TVoice>(TVoice voice, Func<TVoice, int> calls)
        where TVoice : IInsistentAnnouncer
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var store = new InMemoryTimerStore();
        var channels = new RecordingChannels();
        var now = time.GetUtcNow().UtcDateTime;
        await store.ArmAsync(new ArmedTimer
        {
            Id = "pasta", TenantId = Home, Owner = new global::Domain.DTOs.Channel.Account(Home, "nuria"),
            Target = new AnnounceTarget { Room = "Salón" }, DurationSeconds = 1, CreatedAtUtc = now, FiresAtUtc = now.AddSeconds(1)
        });
        var service = new TimerFireService(store, new OneVoice(voice), channels, time, NullLogger<TimerFireService>.Instance);
        await service.StartAsync(CancellationToken.None);
        time.Advance(TimeSpan.FromSeconds(2));
        await WaitUntilAsync(() => calls(voice) == 1, TimeSpan.FromSeconds(5));
        await Eventually.Settle();
        await service.StopAsync(CancellationToken.None);
        return (channels, voice);
    }

    // The satellite it names is off when it comes due: the voice server takes the ring and says every
    // target is offline, so nothing would be heard. It rings on its owner's channels instead, as a timer
    // with no satellite does (user's decision, pass 18: "pasta" in Casa Vidal's Salón rang nowhere).
    [Fact]
    public async Task ExecuteAsync_EverySatelliteItNamesOffline_RingsOnItsOwnersChannels()
    {
        var (channels, _) = await FireOneAsync(AnnouncementOutcome.Offline);

        channels.Rung.ShouldBe(["pasta"]);
    }

    // The household's voice server itself is unreachable when it comes due — its kit restarting, the
    // house without power, the household suspended: nothing would be heard either, so it rings on its
    // owner's channels too (user's decision, pass 19: "horno" in Casa Vidal's Salón rang nowhere).
    [Fact]
    public async Task ExecuteAsync_TheVoiceServerUnreachable_RingsOnItsOwnersChannels()
    {
        var (channels, _) = await FireOneAsync(new UnreachableAnnouncer(), voice => voice.Calls);

        channels.Rung.ShouldBe(["pasta"]);
    }

    [Fact]
    public async Task ExecuteAsync_ASatelliteItNamesRinging_DoesNotRingOnChannelsToo()
    {
        var (channels, _) = await FireOneAsync(AnnouncementOutcome.Started, AnnouncementOutcome.Offline);

        channels.Rung.ShouldBeEmpty();
    }

    [Fact]
    public async Task ExecuteAsync_DueTimer_RingsAsInsistentTimerAlert()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var store = new InMemoryTimerStore();
        var announcer = new RecordingAnnouncer();
        var now = time.GetUtcNow().UtcDateTime;
        await store.ArmAsync(new ArmedTimer
        {
            Id = "pasta",
            TenantId = Home,
            Text = "pasta is ready",
            Target = new AnnounceTarget { Room = "Kitchen" },
            DurationSeconds = 5,
            CreatedAtUtc = now,
            FiresAtUtc = now.AddSeconds(5)
        });
        var service = new TimerFireService(store, new OneVoice(announcer), new NoChannels(), time, NullLogger<TimerFireService>.Instance);

        await service.StartAsync(CancellationToken.None);
        time.Advance(TimeSpan.FromSeconds(4));
        await Eventually.Settle();
        announcer.Requests.ShouldBeEmpty(); // not due yet

        time.Advance(TimeSpan.FromSeconds(2));
        await WaitUntilAsync(() => announcer.Requests.Count == 1, TimeSpan.FromSeconds(5));

        var request = announcer.Requests[0];
        request.Kind.ShouldBe(AnnounceKind.Timer);
        request.Text.ShouldBe("pasta is ready");
        request.Target.Room.ShouldBe("Kitchen");
        request.Insistent.ShouldNotBeNull();
        request.Insistent!.GapSeconds.ShouldBe(10);
        request.Insistent.MaxRepeats.ShouldBe(12);
        request.Insistent.RampStartPercent.ShouldBe(100); // kitchen timers ring at full volume from round 1
        (await store.GetAsync(Home, "pasta")).ShouldBeNull(); // fire-once

        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task ExecuteAsync_TimerWithoutText_SpeaksIdAsName()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var store = new InMemoryTimerStore();
        var announcer = new RecordingAnnouncer();
        var now = time.GetUtcNow().UtcDateTime;
        await store.ArmAsync(new ArmedTimer
        {
            Id = "pasta",
            TenantId = Home,
            Target = new AnnounceTarget { Room = "Kitchen" },
            DurationSeconds = 1,
            CreatedAtUtc = now,
            FiresAtUtc = now.AddSeconds(1)
        });
        var service = new TimerFireService(store, new OneVoice(announcer), new NoChannels(), time, NullLogger<TimerFireService>.Instance);

        await service.StartAsync(CancellationToken.None);
        time.Advance(TimeSpan.FromSeconds(2));
        await WaitUntilAsync(() => announcer.Requests.Count == 1, TimeSpan.FromSeconds(5));

        announcer.Requests[0].Text.ShouldBe("pasta timer");
        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task ExecuteAsync_AnnouncerThrows_LoopSurvives()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var store = new InMemoryTimerStore();
        var announcer = new ThrowingThenRecordingAnnouncer();
        var now = time.GetUtcNow().UtcDateTime;
        await store.ArmAsync(new ArmedTimer
        {
            Id = "first", TenantId = Home, Target = new AnnounceTarget { Room = "Ghost" }, DurationSeconds = 1,
            CreatedAtUtc = now, FiresAtUtc = now.AddSeconds(1)
        });
        await store.ArmAsync(new ArmedTimer
        {
            Id = "second", TenantId = Home, Target = new AnnounceTarget { Room = "Kitchen" }, DurationSeconds = 3,
            CreatedAtUtc = now, FiresAtUtc = now.AddSeconds(3)
        });
        var service = new TimerFireService(store, new OneVoice(announcer), new NoChannels(), time, NullLogger<TimerFireService>.Instance);

        await service.StartAsync(CancellationToken.None);
        time.Advance(TimeSpan.FromSeconds(2));
        await WaitUntilAsync(() => announcer.Calls >= 1, TimeSpan.FromSeconds(5)); // first fires, throws
        time.Advance(TimeSpan.FromSeconds(2));
        await WaitUntilAsync(() => announcer.Succeeded.Count == 1, TimeSpan.FromSeconds(5)); // loop survived

        announcer.Succeeded[0].ShouldBe("second timer");
        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task ExecuteAsync_AnnouncerTimesOut_LoopSurvives()
    {
        // HttpClient's request timeout throws TaskCanceledException — an OperationCanceledException —
        // even though the host stopping token is not cancelled. The poll loop must treat that as a
        // ring failure, not as shutdown, or one hung hub call kills all future timers.
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var store = new InMemoryTimerStore();
        var announcer = new TimeoutThenRecordingAnnouncer();
        var now = time.GetUtcNow().UtcDateTime;
        await store.ArmAsync(new ArmedTimer
        {
            Id = "first", TenantId = Home, Target = new AnnounceTarget { Room = "Slow" }, DurationSeconds = 1,
            CreatedAtUtc = now, FiresAtUtc = now.AddSeconds(1)
        });
        await store.ArmAsync(new ArmedTimer
        {
            Id = "second", TenantId = Home, Target = new AnnounceTarget { Room = "Kitchen" }, DurationSeconds = 3,
            CreatedAtUtc = now, FiresAtUtc = now.AddSeconds(3)
        });
        var service = new TimerFireService(store, new OneVoice(announcer), new NoChannels(), time, NullLogger<TimerFireService>.Instance);

        await service.StartAsync(CancellationToken.None);
        time.Advance(TimeSpan.FromSeconds(2));
        await WaitUntilAsync(() => announcer.Calls >= 1, TimeSpan.FromSeconds(5)); // first times out
        time.Advance(TimeSpan.FromSeconds(2));
        await WaitUntilAsync(() => announcer.Succeeded.Count == 1, TimeSpan.FromSeconds(5)); // loop survived

        announcer.Succeeded[0].ShouldBe("second timer");
        await service.StopAsync(CancellationToken.None);
    }

    private sealed class TimeoutThenRecordingAnnouncer : IInsistentAnnouncer
    {
        private int _calls;
        private readonly List<string> _succeeded = [];
        public int Calls => Volatile.Read(ref _calls);
        public IReadOnlyList<string> Succeeded
        {
            get { lock (_succeeded) { return _succeeded.ToList(); } }
        }
        public Task<AnnounceResponse> StartAsync(AnnounceRequest request, CancellationToken ct)
        {
            Interlocked.Increment(ref _calls);
            if (request.Target.Room == "Slow")
            {
                // The HttpClient 100s timeout: a TaskCanceledException NOT tied to `ct`.
                throw new TaskCanceledException("request timed out");
            }
            lock (_succeeded)
            { _succeeded.Add(request.Text); }
            return Task.FromResult(new AnnounceResponse { AnnouncementId = "a1", Satellites = [] });
        }
    }

    private sealed class ThrowingThenRecordingAnnouncer : IInsistentAnnouncer
    {
        private int _calls;
        private readonly List<string> _succeeded = [];
        public int Calls => Volatile.Read(ref _calls);
        public IReadOnlyList<string> Succeeded
        {
            get { lock (_succeeded) { return _succeeded.ToList(); } }
        }
        public Task<AnnounceResponse> StartAsync(AnnounceRequest request, CancellationToken ct)
        {
            Interlocked.Increment(ref _calls);
            if (request.Target.Room == "Ghost")
            {
                // A cross-process fire failure surfaces as a plain HTTP error, not a hub-specific type.
                throw new InvalidOperationException("no such room");
            }
            lock (_succeeded)
            { _succeeded.Add(request.Text); }
            return Task.FromResult(new AnnounceResponse { AnnouncementId = "a1", Satellites = [] });
        }
    }
}