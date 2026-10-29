using Domain.DTOs.Channel;

namespace Domain.Contracts;

// How a channel server learns whose account the identity it saw is: the accounts service answers,
// asked with the credential the server already holds. Null is an answer — nobody holds that
// identity — and a call the service could not answer throws instead, so a caller never mistakes an
// outage for a stranger.
public interface IAccountResolver
{
    Task<ResolvedAccount?> ResolveAsync(string channel, string identity, CancellationToken ct);
}