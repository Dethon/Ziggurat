using Dashboard.Client.Effects;
using Dashboard.Client.Metrics;
using Dashboard.Client.Services;
using Dashboard.Client.State.Connection;
using Dashboard.Client.State.Errors;
using Dashboard.Client.State.Health;
using Dashboard.Client.State.Latency;
using Dashboard.Client.State.Memory;
using Dashboard.Client.State.Metrics;
using Dashboard.Client.State.Schedules;
using Dashboard.Client.State.Tokens;
using Dashboard.Client.State.Tools;
using Dashboard.Client.State.Voice;
using Domain.DTOs.Metrics;
using Domain.DTOs.Metrics.Enums;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Tests.Unit.Dashboard.Client.Fixtures;

namespace Tests.Unit.Dashboard.Client.Services;

public sealed class MetricsLiveConnectionTests : IAsyncDisposable
{
    private readonly FakeMetricsHubConnection _hub = new();
    private readonly FakeApiHandler _handler = new();
    private readonly FakeTimeProvider _time = new();
    private readonly TokensStore _tokensStore = new();
    private readonly ToolsStore _toolsStore = new();
    private readonly ErrorsStore _errorsStore = new();
    private readonly SchedulesStore _schedulesStore = new();
    private readonly MetricsStore _metricsStore = new();
    private readonly HealthStore _healthStore = new();
    private readonly ConnectionStore _connectionStore = new();
    private readonly MemoryStore _memoryStore = new();
    private readonly LatencyStore _latencyStore = new();
    private readonly VoiceStore _voiceStore = new();
    private readonly MetricFamilyTable _families;
    private readonly DataLoadEffect _dataLoad;
    private readonly RecordingMetricsCatchUp _catchUp;
    private readonly MetricsLiveConnection _liveConnection;

    public MetricsLiveConnectionTests()
    {
        var http = new HttpClient(_handler) { BaseAddress = new Uri("http://localhost") };
        var api = new MetricsApiService(http);
        _families = new MetricFamilyTable(
            api, _tokensStore, _toolsStore, _errorsStore, _schedulesStore,
            _memoryStore, _latencyStore, _voiceStore);
        var binder = new MetricsHubBinder(_families, _metricsStore, _healthStore, NullLogger<MetricsHubBinder>.Instance);
        var overview = new OverviewFigures(api, _metricsStore, _healthStore);
        _dataLoad = new DataLoadEffect(_families, overview, binder);
        _catchUp = new RecordingMetricsCatchUp(new MetricsCatchUp(_families, overview));
        _liveConnection = new MetricsLiveConnection(
            _hub, binder, _connectionStore, _catchUp, _dataLoad, _time,
            NullLogger<MetricsLiveConnection>.Instance);
    }

    public async ValueTask DisposeAsync()
    {
        await _liveConnection.DisposeAsync();
        _tokensStore.Dispose();
        _toolsStore.Dispose();
        _errorsStore.Dispose();
        _schedulesStore.Dispose();
        _metricsStore.Dispose();
        _healthStore.Dispose();
        _connectionStore.Dispose();
        _memoryStore.Dispose();
        _latencyStore.Dispose();
        _voiceStore.Dispose();
    }

    // The retry loop delays through the injected time provider, so nothing here waits in real time:
    // push the clock forward until the connect the module is driving on its own has completed.
    private Task ConnectAsync() => FinishAsync(_liveConnection.ConnectAsync());

    private async Task FinishAsync(Task connecting)
    {
        foreach (var _ in Enumerable.Range(0, 50))
        {
            if (connecting.IsCompleted)
            {
                break;
            }

            _time.Advance(TimeSpan.FromSeconds(30));
            await Task.Delay(1);
        }

        await connecting;
    }

    private Task RaiseVoiceAsync(string satelliteId) =>
        _hub.RaiseAsync("OnVoice", new VoiceEvent
        {
            Metric = VoiceMetric.UtteranceTranscribed,
            SatelliteId = satelliteId,
        });

    // Connecting for the first time and having lost a connection are different things to be told,
    // because they say whether to wait or to go and check the agent.
    [Fact]
    public async Task ConnectAsync_TheHubHasNotAnsweredYet_ReportsConnecting()
    {
        // The gate is what holds the hub unanswered: without it the retry after a failed start runs
        // on a pool thread with a zero-length first delay, so the module could already be live by
        // the time the assertion below reads the store.
        _hub.StartGate = new TaskCompletionSource();
        _hub.FailedStartsRemaining = 1;

        var connecting = _liveConnection.ConnectAsync();
        await WaitForAsync(() => _hub.StartAttempts >= 1);

        _connectionStore.State.Status.ShouldBe(ConnectionStatus.Connecting);
        _hub.StartGate.SetResult();
        await FinishAsync(connecting);
    }

    // A dashboard opened while the agent is still starting: nobody up the stack retries, and the
    // module connects on its own once the hub answers.
    [Fact]
    public async Task ConnectAsync_TheHubIsUnavailableAtFirst_ConnectsOnItsOwnWithNoCallerRetrying()
    {
        _hub.FailedStartsRemaining = 3;

        await ConnectAsync();

        _hub.StartAttempts.ShouldBe(4);
        _connectionStore.State.Status.ShouldBe(ConnectionStatus.Live);
    }

    // Handlers are bound once, before the first start attempt. Rebinding per attempt would leave
    // every handler registered as many times as the start was retried. The event arriving at all
    // also covers the old latch defect: the started latch was set before the work, so a failed
    // first start left the module believing it was running and every later call did nothing.
    [Fact]
    public async Task ConnectAsync_AfterRetriedStarts_BindsEachHandlerOnce()
    {
        _hub.FailedStartsRemaining = 3;

        await ConnectAsync();

        await RaiseVoiceAsync("kitchen-01");

        _voiceStore.State.Events.Count(e => e.SatelliteId == "kitchen-01").ShouldBe(1);
    }

    [Fact]
    public async Task ConnectAsync_AfterAReconnect_AServerPushStillReachesTheStore()
    {
        await ConnectAsync();
        await _hub.RaiseReconnectingAsync(null);
        await _hub.RaiseReconnectedAsync();

        await RaiseVoiceAsync("kitchen-01");

        _voiceStore.State.Events.ShouldContain(e => e.SatelliteId == "kitchen-01");
    }

    [Fact]
    public async Task ConnectAsync_TheTransportIsReconnecting_ReportsReconnecting()
    {
        await ConnectAsync();

        await _hub.RaiseReconnectingAsync(null);

        _connectionStore.State.Status.ShouldBe(ConnectionStatus.Reconnecting);
    }

    [Fact]
    public async Task ConnectAsync_AfterAReconnect_ReportsTheDashboardLiveAgain()
    {
        await ConnectAsync();
        await _hub.RaiseReconnectingAsync(null);

        await _hub.RaiseReconnectedAsync();

        _connectionStore.State.Status.ShouldBe(ConnectionStatus.Live);
    }

    // The headline: recovering from an outage means the dashboard holds what it missed, asserted
    // at the store rather than by recording that a method was called.
    [Fact]
    public async Task Reconnected_EventsArrivedDuringTheOutage_TheStoreHoldsWhatItMissed()
    {
        await ConnectAsync();
        _voiceStore.State.Events.ShouldBeEmpty();
        _handler.AnswerFor("api/metrics/voice?", new List<VoiceEventPayload>
        {
            new((int)VoiceMetric.UtteranceTranscribed, "kitchen-01"),
        });

        await _hub.RaiseReconnectingAsync(null);
        await _hub.RaiseReconnectedAsync();

        _voiceStore.State.Events.ShouldContain(e => e.SatelliteId == "kitchen-01");
    }

    // Ordinary page load fetches the same data on the first connection, so catching up there would
    // double every request on first paint.
    [Fact]
    public async Task ConnectAsync_FirstConnect_DoesNotCatchUp()
    {
        await ConnectAsync();

        _catchUp.Runs.ShouldBe(0);
        _connectionStore.State.Epoch.ShouldBe(1);
    }

    // Opening the dashboard during an outage: the page load failed silently, so the premise behind
    // skipping the first epoch's catch-up does not hold, and the first connection is exactly when
    // the data can finally arrive.
    [Fact]
    public async Task ConnectAsync_TheInitialPageLoadFailed_TheFirstConnectionCatchesUp()
    {
        await _dataLoad.LoadAsync(new DateOnly(2026, 3, 1), new DateOnly(2026, 3, 2), _families.All);
        _handler.AnswerFor("api/metrics/voice?", new List<VoiceEventPayload>
        {
            new((int)VoiceMetric.UtteranceTranscribed, "kitchen-01"),
        });

        await ConnectAsync();

        _catchUp.Runs.ShouldBe(1);
        _voiceStore.State.Events.ShouldContain(e => e.SatelliteId == "kitchen-01");
    }

    // The premise can fail the other way round too: the hub connects fast and the initial load is
    // still in flight when the first epoch decides to skip. The load settling as a failure is what
    // asks for the catch-up the skip assumed the load would deliver.
    [Fact]
    public async Task LoadAsync_TheFirstLoadFailsAfterTheHubBecameLive_CatchesUpOnItsCompletion()
    {
        await ConnectAsync();
        _handler.AnswerFor("api/metrics/voice?", new List<VoiceEventPayload>
        {
            new((int)VoiceMetric.UtteranceTranscribed, "kitchen-01"),
        });

        await _dataLoad.LoadAsync(new DateOnly(2026, 3, 1), new DateOnly(2026, 3, 2), _families.All);

        _catchUp.Runs.ShouldBe(1);
        _voiceStore.State.Events.ShouldContain(e => e.SatelliteId == "kitchen-01");
    }

    // That answer is given once. A later load that fails is an ordinary failed request whose values
    // stay at their last known state, not a missed catch-up.
    [Fact]
    public async Task LoadAsync_ALaterLoadFailsAfterTheFirstEpochWasSettled_DoesNotCatchUpAgain()
    {
        await ConnectAsync();
        await _dataLoad.LoadAsync(new DateOnly(2026, 3, 1), new DateOnly(2026, 3, 2), _families.All);

        await _dataLoad.LoadAsync(new DateOnly(2026, 3, 1), new DateOnly(2026, 3, 2), _families.All);

        _catchUp.Runs.ShouldBe(1);
    }

    // The reconnect landed before the first load's outcome was known and ran the catch-up the skip
    // was waiting to hear about. That settles the premise: the first load failing afterwards is an
    // ordinary failed request, not a second missed catch-up.
    [Fact]
    public async Task LoadAsync_TheFirstLoadFailsAfterAReconnectAlreadyCaughtUp_DoesNotCatchUpAgain()
    {
        await ConnectAsync();

        await _hub.RaiseReconnectingAsync(null);
        await _hub.RaiseReconnectedAsync();

        await _dataLoad.LoadAsync(new DateOnly(2026, 3, 1), new DateOnly(2026, 3, 2), _families.All);

        _catchUp.Runs.ShouldBe(1);
    }

    // The page-load path replaces the same lists a catch-up does, but only catch-up used to hold
    // pushes against it. A push landing while an ordinary load is still in flight was applied
    // straight to the store and then erased when the load's older snapshot replaced the list.
    [Fact]
    public async Task LoadAsync_APushArrivesWhileTheLoadIsInFlight_TheStaleSnapshotCannotEraseIt()
    {
        await ConnectAsync();
        StageEveryRequestExceptVoiceEvents();
        // The only request left unanswered by StageEveryRequestExceptVoiceEvents, so this is the
        // one response the delay lands on.
        _handler.EnqueueResponse(new List<VoiceEventPayload>(), TimeSpan.FromMilliseconds(30));

        var loading = _dataLoad.LoadAsync(new DateOnly(2026, 3, 1), new DateOnly(2026, 3, 2), _families.All);
        await RaiseVoiceAsync("pantry-01");
        await loading;

        _voiceStore.State.Events.ShouldContain(e => e.SatelliteId == "pantry-01");
    }

    // Every endpoint a page load reaches for except the voice events list, which the caller answers
    // separately — usually delayed, so a push can land while that one request is still in flight.
    private void StageEveryRequestExceptVoiceEvents()
    {
        _handler.AnswerFor("api/metrics/summary", Summary(inputTokens: 0));
        _handler.AnswerFor("api/metrics/health", new List<ServiceHealthResponse>());

        new[]
        {
            "api/metrics/tokens?", "api/metrics/tools?", "api/metrics/errors/range?",
            "api/metrics/schedules?", "api/metrics/memory/recall?", "api/metrics/memory/extraction?",
            "api/metrics/memory/dreaming?", "api/metrics/latency?", "api/metrics/latency/trend?",
        }.ToList().ForEach(fragment => _handler.AnswerFor(fragment, Array.Empty<object>()));

        new[]
        {
            "tokens/by/", "tools/by/", "errors/by/", "schedules/by/",
            "memory/by/", "latency/by/", "voice/by/",
        }.ToList().ForEach(fragment => _handler.AnswerFor(fragment, new Dictionary<string, decimal>()));
    }

    [Fact]
    public async Task Reconnected_AfterAnInterruption_CatchesUpOnce()
    {
        await ConnectAsync();

        await _hub.RaiseReconnectingAsync(null);
        await _hub.RaiseReconnectedAsync();

        _catchUp.Runs.ShouldBe(1);
        _connectionStore.State.Epoch.ShouldBe(2);
    }

    // Awaited as part of becoming live rather than detached, so a completed reconnect means what is
    // on screen is current.
    [Fact]
    public async Task Reconnected_CatchUpIsStillRunning_HasNotCompletedYet()
    {
        await ConnectAsync();
        _catchUp.Gate = new TaskCompletionSource();

        var reconnected = _hub.RaiseReconnectedAsync();

        reconnected.IsCompleted.ShouldBeFalse();
        _catchUp.Gate.SetResult();
        await reconnected;
    }

    [Fact]
    public async Task Reconnected_CatchUpFails_LeavesTheConnectionLiveAndThePreviousValuesInPlace()
    {
        var lastKnown = new Dictionary<string, decimal> { ["kept"] = 42m };
        _voiceStore.SetBreakdown(lastKnown);
        await ConnectAsync();
        _catchUp.Failure = new HttpRequestException("observability is down");

        await _hub.RaiseReconnectedAsync();

        _connectionStore.State.Status.ShouldBe(ConnectionStatus.Live);
        _voiceStore.State.Breakdown.ShouldBe(lastKnown);
    }

    // The first half of the ordering defect: a push that arrives while catch-up is still waiting
    // for its response used to land in the list and be erased when the older snapshot replaced it.
    [Fact]
    public async Task Reconnected_APushArrivesBeforeTheCatchUpResponseLands_TheOlderSnapshotCannotEraseIt()
    {
        await ConnectAsync();
        _handler.AnswerFor("api/metrics/voice?", new List<VoiceEventPayload>
        {
            new((int)VoiceMetric.UtteranceTranscribed, "kitchen-01"),
        });
        _catchUp.Gate = new TaskCompletionSource();
        await _hub.RaiseReconnectingAsync(null);

        var reconnected = _hub.RaiseReconnectedAsync();
        await RaiseVoiceAsync("pantry-01");
        _catchUp.Gate.SetResult();
        await reconnected;

        _voiceStore.State.Events.ShouldContain(e => e.SatelliteId == "pantry-01");
        _voiceStore.State.Events.ShouldContain(e => e.SatelliteId == "kitchen-01");
    }

    // The gap the hold used to start too late for: SignalR resumes dispatching as soon as the
    // transport is back, and only then runs the Reconnected handlers. A push landing in between was
    // applied unheld and then erased by the catch-up snapshot that followed it.
    [Fact]
    public async Task Reconnecting_APushArrivesBeforeTheReconnectedHandlerRuns_TheSnapshotCannotEraseIt()
    {
        await ConnectAsync();
        _handler.AnswerFor("api/metrics/voice?", new List<VoiceEventPayload>
        {
            new((int)VoiceMetric.UtteranceTranscribed, "kitchen-01"),
        });
        await _hub.RaiseReconnectingAsync(null);

        await RaiseVoiceAsync("pantry-01");
        await _hub.RaiseReconnectedAsync();

        _voiceStore.State.Events.ShouldContain(e => e.SatelliteId == "pantry-01");
        _voiceStore.State.Events.ShouldContain(e => e.SatelliteId == "kitchen-01");
    }

    // The second half: a push the snapshot already contains, arriving after the lists were
    // replaced, used to be appended on top of its own copy.
    [Fact]
    public async Task Reconnected_TheSnapshotAlreadyContainsAPushedEvent_TheStoreHoldsItOnce()
    {
        var stamped = new DateTimeOffset(2026, 3, 24, 12, 0, 0, TimeSpan.Zero);
        await ConnectAsync();
        _handler.AnswerFor("api/metrics/voice?", new List<StampedVoiceEventPayload>
        {
            new((int)VoiceMetric.UtteranceTranscribed, "kitchen-01", stamped),
        });
        _catchUp.GateAfter = new TaskCompletionSource();
        await _hub.RaiseReconnectingAsync(null);

        var reconnected = _hub.RaiseReconnectedAsync();
        await WaitForAsync(() => _voiceStore.State.Events.Count > 0);
        await _hub.RaiseAsync("OnVoice", new VoiceEvent
        {
            Metric = VoiceMetric.UtteranceTranscribed,
            SatelliteId = "kitchen-01",
            Timestamp = stamped,
        });
        _catchUp.GateAfter.SetResult();
        await reconnected;

        _voiceStore.State.Events.Count(e => e.SatelliteId == "kitchen-01").ShouldBe(1);
    }

    // The counter half of that same push. The dedupe question is asked of the events snapshot, so
    // the KPI totals have to come from that same snapshot: reading them from a separately timed
    // summary request put the two an instant apart, and an event written in between was either
    // counted twice or lost whole depending on which read was older. Both tests below stage the two
    // reads one instant apart, in either order, and the pushed event has to be counted exactly once.
    [Fact]
    public async Task Reconnected_TheEventsSnapshotIsNewerThanTheSummary_TheKpiRowCountsThePushedEventOnce()
    {
        await ReconnectWithPushedTokenEventAsync(summaryInputTokens: 100, snapshot: [_prior, _pushed]);

        _metricsStore.State.InputTokens.ShouldBe(107);
    }

    [Fact]
    public async Task Reconnected_TheSummaryIsNewerThanTheEventsSnapshot_TheKpiRowCountsThePushedEventOnce()
    {
        await ReconnectWithPushedTokenEventAsync(summaryInputTokens: 107, snapshot: [_prior]);

        _metricsStore.State.InputTokens.ShouldBe(107);
    }

    // One token event the client already knew about before the outage, and one written during it and
    // pushed while catch-up was holding. Whether the pushed one is in the staged events snapshot is
    // what the two tests vary.
    private static readonly DateTimeOffset _priorStamp = new(2026, 3, 24, 11, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset _pushedStamp = new(2026, 3, 24, 12, 0, 0, TimeSpan.Zero);
    private static readonly TokenUsagePayload _prior = new("nabu", "m", 100, 10, 0.10m, _priorStamp);
    private static readonly TokenUsagePayload _pushed = new("nabu", "m", 7, 1, 0.01m, _pushedStamp);

    private async Task ReconnectWithPushedTokenEventAsync(
        long summaryInputTokens, List<TokenUsagePayload> snapshot)
    {
        _handler.AnswerFor("api/metrics/summary", Summary(inputTokens: 0));
        await _dataLoad.LoadAsync(new DateOnly(2026, 3, 1), new DateOnly(2026, 3, 2), _families.All);
        await ConnectAsync();
        _handler.AnswerFor("api/metrics/summary", Summary(summaryInputTokens));
        _handler.AnswerFor("api/metrics/tokens?", snapshot);
        _handler.AnswerFor("api/metrics/tokens/by/Model", new Dictionary<string, decimal>());
        _catchUp.GateAfter = new TaskCompletionSource();
        await _hub.RaiseReconnectingAsync(null);

        var reconnected = _hub.RaiseReconnectedAsync();
        await WaitForAsync(() => _tokensStore.State.Events.Count > 0);
        await _hub.RaiseAsync("OnTokenUsage", new TokenUsageEvent
        {
            Sender = "nabu",
            Model = "m",
            InputTokens = 7,
            OutputTokens = 1,
            Cost = 0.01m,
            Timestamp = _pushedStamp,
        });
        _catchUp.GateAfter.SetResult();
        await reconnected;
    }

    // Health is a roster catch-up now re-reads, so a health push is held like any other. Applied
    // ahead of the snapshot it would be overwritten by a roster taken before the service came back.
    [Fact]
    public async Task Reconnected_AHealthPushArrivesDuringCatchUp_TheOlderRosterCannotOverwriteIt()
    {
        await ConnectAsync();
        _handler.AnswerFor("api/metrics/health", new List<ServiceHealthResponse>
        {
            new("agent", false, "2026-03-24T11:59:00Z"),
        });
        _catchUp.Gate = new TaskCompletionSource();
        await _hub.RaiseReconnectingAsync(null);

        var reconnected = _hub.RaiseReconnectedAsync();
        await _hub.RaiseAsync("OnHealthUpdate", new ServiceHealthUpdate(
            "agent", true, new DateTimeOffset(2026, 3, 24, 12, 0, 0, TimeSpan.Zero)));
        _catchUp.Gate.SetResult();
        await reconnected;

        _healthStore.State.Services.ShouldContain(s => s.Service == "agent" && s.IsHealthy);
    }

    // A close with no Reconnecting before it: the server disallowed automatic reconnect, or the
    // transport was never configured for it. The comment that nothing needs to handle Closed used
    // to be the whole defense; without a restart the module sat dead while the indicator still read
    // Live.
    [Fact]
    public async Task Closed_NoReconnectWasAttempted_TheModuleRestartsAndBecomesLiveAgain()
    {
        await ConnectAsync();
        var attemptsBeforeClose = _hub.StartAttempts;

        await FinishAsync(_hub.RaiseClosedAsync(new InvalidOperationException("server closed the connection")));

        _hub.StartAttempts.ShouldBeGreaterThan(attemptsBeforeClose);
        _connectionStore.State.Status.ShouldBe(ConnectionStatus.Live);
        _catchUp.Runs.ShouldBe(1);
        await RaiseVoiceAsync("kitchen-01");
        _voiceStore.State.Events.ShouldContain(e => e.SatelliteId == "kitchen-01");
    }

    // The same push-survival guarantee a reconnect gives applies to a close-triggered restart: a
    // push landing while the module is starting the hub back up cannot be erased by the catch-up
    // snapshot that follows it.
    [Fact]
    public async Task Closed_APushArrivesWhileRestarting_TheOlderSnapshotCannotEraseIt()
    {
        await ConnectAsync();
        _handler.AnswerFor("api/metrics/voice?", new List<VoiceEventPayload>
        {
            new((int)VoiceMetric.UtteranceTranscribed, "kitchen-01"),
        });
        _catchUp.Gate = new TaskCompletionSource();

        var closed = _hub.RaiseClosedAsync(new InvalidOperationException("server closed the connection"));
        await RaiseVoiceAsync("pantry-01");
        _catchUp.Gate.SetResult();
        await FinishAsync(closed);

        _voiceStore.State.Events.ShouldContain(e => e.SatelliteId == "pantry-01");
        _voiceStore.State.Events.ShouldContain(e => e.SatelliteId == "kitchen-01");
    }

    [Fact]
    public async Task DisposeAsync_AfterConnecting_APushAfterwardsChangesNothing()
    {
        await ConnectAsync();

        await _liveConnection.DisposeAsync();
        await RaiseVoiceAsync("kitchen-01");

        _voiceStore.State.Events.ShouldBeEmpty();
        _hub.Disposed.ShouldBeTrue();
    }

    // Unbinding the pushes was not the whole of letting go: the lifecycle handlers stayed attached,
    // so a reconnect landing after disposal drove a whole become-live sequence on a dead module.
    [Fact]
    public async Task DisposeAsync_TheTransportReconnectsAfterwards_TheModuleDoesNothing()
    {
        await ConnectAsync();

        await _liveConnection.DisposeAsync();
        await _hub.RaiseReconnectingAsync(null);
        await _hub.RaiseReconnectedAsync();

        _catchUp.Runs.ShouldBe(0);
        _connectionStore.State.Epoch.ShouldBe(1);
        _connectionStore.State.Status.ShouldBe(ConnectionStatus.Live);
    }

    // The delay between retries used to run out its full schedule even after disposal, because
    // nothing tied it to the module going away. Wiring the disposal token into it is what lets
    // dispose return without the fake clock ever being advanced here.
    [Fact]
    public async Task DisposeAsync_DuringTheRetryDelay_TheDelayIsCancelledInsteadOfWaitedOut()
    {
        _hub.FailedStartsRemaining = 100;
        var connecting = _liveConnection.ConnectAsync();
        await WaitForAsync(() => _hub.StartAttempts >= 1);

        await _liveConnection.DisposeAsync();

        var settled = await Task.WhenAny(connecting, Task.Delay(TimeSpan.FromSeconds(5)));
        settled.ShouldBeSameAs(connecting);
    }

    [Fact]
    public async Task DisposeAsync_WhileTheHubIsStillUnavailable_StopsTryingAndPublishesNothing()
    {
        _hub.FailedStartsRemaining = 100;
        var connecting = _liveConnection.ConnectAsync();

        await _liveConnection.DisposeAsync();
        await FinishAsync(connecting);

        _connectionStore.State.Status.ShouldBe(ConnectionStatus.Connecting);
        _connectionStore.State.Epoch.ShouldBe(0);
        _hub.StartAttempts.ShouldBeLessThan(100);
    }

    // The other disposal races end in a failed start, where the loop condition is what stops the
    // module. A start that succeeds after disposal took the become-live path instead — status, epoch,
    // catch-up and a hold on a binder nobody is going to unbind again — on a module that is gone.
    [Fact]
    public async Task DisposeAsync_AStartSucceedsAfterwards_TheModuleBecomesNothing()
    {
        _hub.StartGate = new TaskCompletionSource();
        var connecting = _liveConnection.ConnectAsync();
        await WaitForAsync(() => _hub.StartAttempts >= 1);

        await _liveConnection.DisposeAsync();
        _hub.StartGate.SetResult();
        await FinishAsync(connecting);

        _connectionStore.State.Status.ShouldBe(ConnectionStatus.Connecting);
        _connectionStore.State.Epoch.ShouldBe(0);
        _catchUp.Runs.ShouldBe(0);
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        foreach (var _ in Enumerable.Range(0, 100))
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(1);
        }
    }

    private static MetricsSummary Summary(long inputTokens) =>
        new(inputTokens, OutputTokens: 30, TotalTokens: inputTokens + 30, Cost: 1.5m, ToolCalls: 4, ToolErrors: 1);

    private sealed record VoiceEventPayload(int Metric, string SatelliteId);

    private sealed record TokenUsagePayload(
        string Sender, string Model, int InputTokens, int OutputTokens, decimal Cost, DateTimeOffset Timestamp);

    // Carries the timestamp so the deserialized snapshot event and the pushed event are equal by
    // record value, which is the identity catch-up reconciles by.
    private sealed record StampedVoiceEventPayload(int Metric, string SatelliteId, DateTimeOffset Timestamp);
}