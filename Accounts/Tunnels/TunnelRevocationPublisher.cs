using Accounts.Announcements;
using Domain.DTOs.Tunnel;

namespace Accounts.Tunnels;

// Tells whoever listens that a tunnel credential was revoked, so the relay closes the tunnel it
// opened at once rather than at its next open.
public sealed class TunnelRevocationPublisher(RedisAnnouncer announcer)
{
    // Why the listeners were not told, or null when they were.
    public Task<string?> TellAsync(TunnelCredentialRevoked revoked, CancellationToken ct) =>
        announcer.AnnounceAsync(TunnelCredentialProtocol.RevokedChannel, revoked, ct);
}