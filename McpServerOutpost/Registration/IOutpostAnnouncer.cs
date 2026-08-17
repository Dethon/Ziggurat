using Domain.DTOs;

namespace McpServerOutpost.Registration;

// What one keepalive learned. Lapsed is the interesting one: the machine went quiet long enough
// for the hub to forget it — a suspend, a long network outage — and the answer is to announce
// itself afresh rather than to keep pinging an entry nobody holds.
public enum KeepAliveOutcome
{
    Refreshed,
    Lapsed,
    Unreachable
}

// The keepalive's whole answer: whether the registration is still held, and what the hub made of
// this outpost's mount the last time an opted-in agent built a session. The verdict is the only
// thing that ever comes back this way, and it stays that way — an outpost reports no telemetry of
// its own and the keepalive is not the place to start.
public sealed record KeepAliveAnswer(KeepAliveOutcome Outcome, OutpostVerdict Verdict)
{
    public static readonly KeepAliveAnswer Unreachable =
        new(KeepAliveOutcome.Unreachable, OutpostVerdict.Unknown);

    public static readonly KeepAliveAnswer Lapsed =
        new(KeepAliveOutcome.Lapsed, OutpostVerdict.Unknown);
}

// The three calls the machine makes at the hub, behind an interface so the loop that drives them
// is provable without a hub, a network or a machine.
internal interface IOutpostAnnouncer
{
    Task<bool> RegisterAsync(OutpostRegistration registration, CancellationToken ct);

    Task<KeepAliveAnswer> KeepAliveAsync(string name, CancellationToken ct);

    Task DeregisterAsync(string name, CancellationToken ct);
}