using Domain.DTOs.Channel;

namespace Domain.Contracts;

// How a channel server learns whose account the identity it saw is: the accounts service answers,
// asked with the credential the server already holds. Null is an answer — nobody holds that
// identity — and a call the service could not answer throws instead, so a caller never mistakes an
// outage for a stranger. Every answer carries the service's assertion of the person, which is what
// the agent believes about the turn the channel then starts.
public interface IAccountResolver
{
    Task<ResolvedAccount?> ResolveAsync(string channel, string identity, CancellationToken ct);

    // The household's guest, signed for a channel that hears somebody nobody sent (a satellite).
    // Throws like the rest when the service could not answer: an outage is never a guest.
    Task<ResolvedGuest> GuestAsync(string channel, CancellationToken ct);
}