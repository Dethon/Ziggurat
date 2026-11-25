using Domain.DTOs.Channel;

namespace Domain.Contracts;

// The apps server's two questions of the accounts service, asked with its own credential. Redeeming:
// the app sign-in's code a browser brought back to a built app's origin, with that origin, answered
// once with the person of the session it was issued for and an assertion on `apps` naming that session
// (`sid`), or nobody (null) for a code unknown, spent, run out, issued for another origin, or whose
// session ended. Renewing: that session's person again, with a fresh `apps` assertion while the session
// lives and nobody once it is revoked — as the chat hub keeps a connection's person. A call the service
// could not answer throws, so an outage is never taken for a refusal or a revocation.
public interface IAppSignIns
{
    Task<ResolvedAccount?> RedeemAsync(string code, string origin, CancellationToken ct);

    Task<ResolvedAccount?> RenewAsync(string userId, string sessionId, CancellationToken ct);
}