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

// The three calls the machine makes at the hub, behind an interface so the loop that drives them
// is provable without a hub, a network or a machine.
internal interface IOutpostAnnouncer
{
    Task<bool> RegisterAsync(OutpostRegistration registration, CancellationToken ct);

    Task<KeepAliveOutcome> KeepAliveAsync(string name, CancellationToken ct);

    Task DeregisterAsync(string name, CancellationToken ct);
}