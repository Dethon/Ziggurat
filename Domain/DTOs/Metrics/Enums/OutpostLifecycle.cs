namespace Domain.DTOs.Metrics.Enums;

// An outpost is there while its tunnel is open, so its life is the tunnel's: it opened, and it
// closed — the reason saying whether the machine stopped, lost its network, reconnected over its own
// old opening or had its credential revoked.
public enum OutpostLifecycle
{
    Opened,
    Closed
}