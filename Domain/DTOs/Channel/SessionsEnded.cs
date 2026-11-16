namespace Domain.DTOs.Channel;

// The accounts service's word that some of a person's sessions ended — a device signed out from the
// account page, a passkey revoked with every session it began, the person disabled — published on
// EndedChannel. It names the person, not the sessions: a listener asks the service which of the ones
// it holds still live, so one lost announcement's worth of trust is all it ever extends.
public sealed record SessionsEnded(string TenantId, string UserId)
{
    public const string EndedChannel = "accounts:sessions:ended";
}