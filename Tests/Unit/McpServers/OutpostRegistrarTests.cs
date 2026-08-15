using Domain.DTOs;
using Domain.Outposts;
using McpServerOutpost.Registration;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

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
        hub.KeepAliveAnswer = KeepAliveOutcome.Lapsed;
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

    private static (OutpostRegistrar Registrar, RecordingHub Hub, ArmedClock Clock) Registrar()
    {
        var clock = new ArmedClock(DateTimeOffset.Parse("2026-08-15T14:00:00Z"));
        var hub = new RecordingHub();
        return (new OutpostRegistrar(hub, _laptop, clock, NullLogger<OutpostRegistrar>.Instance), hub, clock);
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
        public KeepAliveOutcome KeepAliveAnswer { get; set; } = KeepAliveOutcome.Refreshed;

        public IReadOnlyList<OutpostRegistration> Registrations => Snapshot(_registrations);
        public IReadOnlyList<string> KeepAlives => Snapshot(_keepAlives);
        public IReadOnlyList<string> Deregistrations => Snapshot(_deregistrations);

        public Task<bool> RegisterAsync(OutpostRegistration registration, CancellationToken ct)
        {
            lock (_gate)
            {
                _registrations.Add(registration);
            }

            return Task.FromResult(!RefuseRegistrations);
        }

        public Task<KeepAliveOutcome> KeepAliveAsync(string name, CancellationToken ct)
        {
            lock (_gate)
            {
                _keepAlives.Add(name);
            }

            return Task.FromResult(KeepAliveAnswer);
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

        private IReadOnlyList<T> Snapshot<T>(List<T> source)
        {
            lock (_gate)
            {
                return [.. source];
            }
        }
    }
}