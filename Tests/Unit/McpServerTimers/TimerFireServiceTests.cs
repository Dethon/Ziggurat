using Domain.Contracts;
using Domain.DTOs.Voice;
using Infrastructure.Timers;
using McpServerTimers.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Shouldly;

namespace Tests.Unit.McpServerTimers;

public class TimerFireServiceTests
{
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
            Text = "pasta is ready",
            Target = new AnnounceTarget { Room = "Kitchen" },
            DurationSeconds = 5,
            CreatedAtUtc = now,
            FiresAtUtc = now.AddSeconds(5)
        });
        var service = new TimerFireService(store, announcer, time, NullLogger<TimerFireService>.Instance);

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
        (await store.GetAsync("pasta")).ShouldBeNull(); // fire-once

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
            Target = new AnnounceTarget { Room = "Kitchen" },
            DurationSeconds = 1,
            CreatedAtUtc = now,
            FiresAtUtc = now.AddSeconds(1)
        });
        var service = new TimerFireService(store, announcer, time, NullLogger<TimerFireService>.Instance);

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
            Id = "first", Target = new AnnounceTarget { Room = "Ghost" }, DurationSeconds = 1,
            CreatedAtUtc = now, FiresAtUtc = now.AddSeconds(1)
        });
        await store.ArmAsync(new ArmedTimer
        {
            Id = "second", Target = new AnnounceTarget { Room = "Kitchen" }, DurationSeconds = 3,
            CreatedAtUtc = now, FiresAtUtc = now.AddSeconds(3)
        });
        var service = new TimerFireService(store, announcer, time, NullLogger<TimerFireService>.Instance);

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
            Id = "first", Target = new AnnounceTarget { Room = "Slow" }, DurationSeconds = 1,
            CreatedAtUtc = now, FiresAtUtc = now.AddSeconds(1)
        });
        await store.ArmAsync(new ArmedTimer
        {
            Id = "second", Target = new AnnounceTarget { Room = "Kitchen" }, DurationSeconds = 3,
            CreatedAtUtc = now, FiresAtUtc = now.AddSeconds(3)
        });
        var service = new TimerFireService(store, announcer, time, NullLogger<TimerFireService>.Instance);

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