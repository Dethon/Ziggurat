using Domain.DTOs.Channel;

namespace Domain.Contracts;

// How the chat hub keeps a connection's person vouched for: asked for the session the connection
// signed in with, the accounts service answers that session's person with a fresh assertion for the
// hub's channel while the session lives, and nobody (null) once it is revoked or gone — so a session
// revoked elsewhere starts no further turn on a connection still open. Asked with the hub's own
// credential, which may ask this and nothing else; a call the service could not answer throws, so
// an outage is never taken for a revocation.
public interface ISessionAccounts
{
    Task<ResolvedAccount?> SessionAsync(string userId, string sessionId, CancellationToken ct);
}