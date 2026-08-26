using Domain.DTOs;
using Domain.Outposts;
using McpServerOutpost.Registration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Tests.Unit;

namespace Tests.Unit.McpServers;

// The machine side of the lifecycle, driven over an announcer and a clock the test owns. Nothing
// here needs a hub: what is being pinned is that the loop keeps trying, keeps asking, notices when
// the hub has forgotten it, and takes its registration back on the way out.
public class OutpostRegistrarTests
{
    private static readonly OutpostRegistration _laptop = new()
    {
        Name = "laptop",
        Endpoint = "http://192.168.1.20:8099/mcp"
    };

    [Fact]
    public async Task Starting_AnnouncesTheMachineAtOnce()
    {
        var (registrar, hub, _) = Registrar();

        await registrar.StartAsync(CancellationToken.None);

        await Eventually.Until(() => hub.Registrations.Count == 1, "the machine announces itself");
        hub.Registrations[0].ShouldBe(_laptop);
        await registrar.StopAsync(CancellationToken.None);
    }

    // A laptop that booted before the stack, or joined the VPN a minute late, must not be
    // permanently absent for that reason.
    [Fact]
    public async Task AHubThatIsNotThereYet_IsRetriedUntilItAnswers()
    {
        var (registrar, hub, clock) = Registrar();
        hub.RefuseRegistrations = true;
        await registrar.StartAsync(CancellationToken.None);
        await Eventually.Until(() => hub.Registrations.Count == 1, "the first attempt");

        hub.RefuseRegistrations = false;
        await clock.AdvancePastAsync(TimeSpan.FromSeconds(1));

        await Eventually.Until(() => hub.KeepAlives.Count > 0 || hub.Registrations.Count > 1,
            "the machine tries again once the hub is back");
        await registrar.StopAsync(CancellationToken.None);
        hub.Registrations.Count.ShouldBeGreaterThan(1);
    }

    // A hub that takes the connection and then says nothing times the HttpClient out, and that
    // arrives as a TaskCanceledException — the type a real shutdown also throws. Read as a shutdown
    // it ended the process: the registrar faulted, the host stopped with it, and a machine that was
    // serving its files stopped serving them because the hub was slow to answer. A hub that is
    // reachable but not answering is the VPN case, where the listener has to stay up.
    [Fact]
    public async Task AHubThatTimesOutWhileRegistering_KeepsServingAndTriesAgain()
    {
        var (registrar, hub, clock) = Registrar();
        hub.TimeOutRequests = true;
        await registrar.StartAsync(CancellationToken.None);
        await Eventually.Until(() => hub.Registrations.Count == 1, "the attempt that times out");

        // The hub records the attempt and only then throws, so a count of one says the registrar is
        // inside RegisterAsync — not that it has come back out and parked on its retry. Clearing
        // the flag against that count let a loaded run get the order backwards: the registrar armed
        // and the advance fired while the old value was still what RegisterAsync saw, so the retry
        // timed out too and the next wait was a two-second backoff this test never advances past.
        // Waiting for the arm first is the difference between "the attempt started" and "the
        // attempt is over and the code is ready to be interfered with".
        await clock.WaitUntilArmedAsync(TimeSpan.FromSeconds(1));
        hub.TimeOutRequests = false;
        await clock.AdvancePastAsync(TimeSpan.FromSeconds(1));

        await Eventually.Until(() => hub.Registrations.Count > 1, "the machine tries again");
        registrar.ExecuteTask?.IsFaulted.ShouldBe(false);
        await registrar.StopAsync(CancellationToken.None);
    }

    // The same timeout on the other call, where the machine is registered and the hub stops
    // answering. Dropping back to announcing itself is the recovery; ending the process is not one.
    [Fact]
    public async Task AHubThatTimesOutWhileKeepingAlive_DropsBackToAnnouncingItself()
    {
        var (registrar, hub, clock) = Registrar();
        await registrar.StartAsync(CancellationToken.None);
        await Eventually.Until(() => hub.Registrations.Count == 1, "the machine announces itself");

        hub.TimeOutRequests = true;
        await clock.AdvancePastAsync(OutpostLifetime.KeepAliveInterval);
        await Eventually.Until(() => hub.KeepAlives.Count == 1, "the keepalive that times out");

        // Same order as the registering case above: the count says the call started, the armed
        // retry says it finished and parked.
        await clock.WaitUntilArmedAsync(TimeSpan.FromSeconds(1));
        hub.TimeOutRequests = false;
        await clock.AdvancePastAsync(TimeSpan.FromSeconds(1));

        await Eventually.Until(() => hub.Registrations.Count == 2, "the machine announces itself again");
        registrar.ExecuteTask?.IsFaulted.ShouldBe(false);
        await registrar.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task OnceRegistered_ItKeepsItselfAliveEveryInterval()
    {
        var (registrar, hub, clock) = Registrar();
        await registrar.StartAsync(CancellationToken.None);
        await Eventually.Until(() => hub.Registrations.Count == 1, "the machine announces itself");

        await clock.AdvancePastAsync(OutpostLifetime.KeepAliveInterval);
        await Eventually.Until(() => hub.KeepAlives.Count == 1, "the first keepalive");
        await clock.AdvancePastAsync(OutpostLifetime.KeepAliveInterval, previously: 1);
        await Eventually.Until(() => hub.KeepAlives.Count == 2, "the second keepalive");

        await registrar.StopAsync(CancellationToken.None);
        hub.KeepAlives.ShouldAllBe(name => name == "laptop");
    }

    // The hub forgot this machine while it was quiet — a suspend, a long outage. Announcing itself
    // again is the whole recovery: the name is the identity and the last write wins.
    [Fact]
    public async Task AKeepAliveTheHubHasForgotten_MakesTheMachineAnnounceItselfAgain()
    {
        var (registrar, hub, clock) = Registrar();
        hub.Answer = KeepAliveAnswer.Lapsed;
        await registrar.StartAsync(CancellationToken.None);
        await Eventually.Until(() => hub.Registrations.Count == 1, "the machine announces itself");

        await clock.AdvancePastAsync(OutpostLifetime.KeepAliveInterval);
        await Eventually.Until(() => hub.KeepAlives.Count == 1, "the keepalive the hub refuses");
        await clock.AdvancePastAsync(TimeSpan.FromSeconds(1));

        await Eventually.Until(() => hub.Registrations.Count == 2, "the machine announces itself again");
        await registrar.StopAsync(CancellationToken.None);
    }

    // A machine somebody switched off deliberately disappears at once rather than lingering as a
    // mount the agent offers for another ninety seconds.
    [Fact]
    public async Task Stopping_TakesTheRegistrationBack()
    {
        var (registrar, hub, _) = Registrar();
        await registrar.StartAsync(CancellationToken.None);
        await Eventually.Until(() => hub.Registrations.Count == 1, "the machine announces itself");

        await registrar.StopAsync(CancellationToken.None);

        hub.Deregistrations.ShouldBe(["laptop"]);
    }

    // The hub may be the thing that went away, and a shutdown must not hang on it or fail because
    // of it — the registration lapses on its own in that case.
    [Fact]
    public async Task AHubThatCannotBeReachedOnTheWayOut_DoesNotFailTheShutdown()
    {
        var (registrar, hub, _) = Registrar();
        hub.ThrowOnDeregister = true;
        await registrar.StartAsync(CancellationToken.None);

        await Should.NotThrowAsync(() => registrar.StopAsync(CancellationToken.None));
    }

    // Somebody who runs an outpost and finds it never shows up can see why on the computer they
    // are sitting at, instead of needing access to the hub's logs. Prominently enough that
    // somebody watching the process sees it: there is nothing they can do about it from the hub.
    [Fact]
    public async Task AShadowedVerdict_IsReportedAtTheMachineAsAnError()
    {
        var (registrar, hub, clock, logs) = ReportingRegistrar();
        hub.Answer = new KeepAliveAnswer(KeepAliveOutcome.Refreshed, OutpostVerdict.Shadowed);
        await registrar.StartAsync(CancellationToken.None);
        await Eventually.Until(() => hub.Registrations.Count == 1, "the machine announces itself");

        await clock.AdvancePastAsync(OutpostLifetime.KeepAliveInterval);

        await Eventually.Until(
            () => logs.Messages.Any(m => m.Contains("SHADOWED", StringComparison.Ordinal)),
            "the machine says why it is not there");
        await registrar.StopAsync(CancellationToken.None);
    }

    // Not yet known is what every registration reads as until an opted-in agent has built a
    // session, which on a hub nobody is talking to may be a long time. It is not a problem and
    // must not be reported as one.
    [Fact]
    public async Task AVerdictThatIsNotYetKnown_IsReportedAsNothingAtAll()
    {
        var (registrar, hub, clock, logs) = ReportingRegistrar();
        await registrar.StartAsync(CancellationToken.None);
        await Eventually.Until(() => hub.Registrations.Count == 1, "the machine announces itself");

        await clock.AdvancePastAsync(OutpostLifetime.KeepAliveInterval);
        await Eventually.Until(() => hub.KeepAlives.Count == 1, "the keepalive");
        await Eventually.Settle();

        logs.Messages.ShouldNotContain(m => m.Contains("mounted", StringComparison.OrdinalIgnoreCase));
        logs.Messages.ShouldNotContain(m => m.Contains("shadow", StringComparison.OrdinalIgnoreCase));
        await registrar.StopAsync(CancellationToken.None);
    }

    // A machine left running says it once rather than every thirty seconds.
    [Fact]
    public async Task AVerdictThatHasNotChanged_IsSaidOnce()
    {
        var (registrar, hub, clock, logs) = ReportingRegistrar();
        hub.Answer = new KeepAliveAnswer(KeepAliveOutcome.Refreshed, OutpostVerdict.Mounted);
        await registrar.StartAsync(CancellationToken.None);
        await Eventually.Until(() => hub.Registrations.Count == 1, "the machine announces itself");

        await clock.AdvancePastAsync(OutpostLifetime.KeepAliveInterval);
        await Eventually.Until(() => hub.KeepAlives.Count == 1, "the first keepalive");
        await clock.AdvancePastAsync(OutpostLifetime.KeepAliveInterval, previously: 1);
        await Eventually.Until(() => hub.KeepAlives.Count == 2, "the second keepalive");
        await Eventually.Settle();

        logs.Messages.Count(m => m.Contains("is mounted", StringComparison.Ordinal)).ShouldBe(1);
        await registrar.StopAsync(CancellationToken.None);
    }

    private static (OutpostRegistrar Registrar, RecordingHub Hub, ArmedClock Clock) Registrar()
    {
        var clock = new ArmedClock(DateTimeOffset.Parse("2026-08-15T14:00:00Z"));
        var hub = new RecordingHub();
        return (new OutpostRegistrar(hub, _laptop, clock, NullLogger<OutpostRegistrar>.Instance), hub, clock);
    }

    // Captures every level, so a test asserting that nothing was reported as a problem would see
    // an error if one were.
    private static (OutpostRegistrar Registrar, RecordingHub Hub, ArmedClock Clock, CapturingLoggerProvider Logs)
        ReportingRegistrar()
    {
        var clock = new ArmedClock(DateTimeOffset.Parse("2026-08-15T14:00:00Z"));
        var hub = new RecordingHub();
        var logs = new CapturingLoggerProvider(LogLevel.Trace);
        var factory = new LoggerFactory([logs]);
        return (
            new OutpostRegistrar(hub, _laptop, clock, factory.CreateLogger<OutpostRegistrar>()),
            hub, clock, logs);
    }

    // Written from the registrar's loop and read from the test thread, so every reader takes a
    // copy under the lock rather than enumerating the live list.
    private sealed class RecordingHub : IOutpostAnnouncer
    {
        private readonly Lock _gate = new();
        private readonly List<OutpostRegistration> _registrations = [];
        private readonly List<string> _keepAlives = [];
        private readonly List<string> _deregistrations = [];

        public bool RefuseRegistrations { get; set; }
        public bool ThrowOnDeregister { get; set; }

        // What an HttpClient whose timeout elapsed throws, down to the inner exception: a
        // cancellation nobody asked for, which is the whole reason it can be mistaken for one
        // somebody did.
        public bool TimeOutRequests { get; set; }
        public KeepAliveAnswer Answer { get; set; } =
            new(KeepAliveOutcome.Refreshed, OutpostVerdict.Unknown);

        public IReadOnlyList<OutpostRegistration> Registrations => Snapshot(_registrations);
        public IReadOnlyList<string> KeepAlives => Snapshot(_keepAlives);
        public IReadOnlyList<string> Deregistrations => Snapshot(_deregistrations);

        public Task<bool> RegisterAsync(OutpostRegistration registration, CancellationToken ct)
        {
            lock (_gate)
            {
                _registrations.Add(registration);
            }

            TimeOutIfAsked();
            return Task.FromResult(!RefuseRegistrations);
        }

        public Task<KeepAliveAnswer> KeepAliveAsync(string name, CancellationToken ct)
        {
            lock (_gate)
            {
                _keepAlives.Add(name);
            }

            TimeOutIfAsked();
            return Task.FromResult(Answer);
        }

        public Task DeregisterAsync(string name, CancellationToken ct)
        {
            if (ThrowOnDeregister)
            {
                throw new HttpRequestException("the hub is gone");
            }

            lock (_gate)
            {
                _deregistrations.Add(name);
            }

            return Task.CompletedTask;
        }

        private void TimeOutIfAsked()
        {
            if (TimeOutRequests)
            {
                throw new TaskCanceledException(
                    "The request was canceled due to the configured HttpClient.Timeout of 10 seconds "
                    + "elapsing.", new TimeoutException());
            }
        }

        private IReadOnlyList<T> Snapshot<T>(List<T> source)
        {
            lock (_gate)
            {
                return [.. source];
            }
        }
    }
}