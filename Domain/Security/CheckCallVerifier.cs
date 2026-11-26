using Domain.Security.Signing;

namespace Domain.Security;

// Who a check reads and asks as (AppReads.CheckCallTokenHeader; ChannelMessageNotification.CheckCallToken):
// the person the agent's own call token names, signed by this agent's key for the tool call that ran
// `./check` on the apps server it dials, and handed back by that server while the check runs. It is the agent's word coming home, so it is checked against
// the agent's own keys, never a fetched copy, and on the wall clock it was signed on. Nobody mints
// anything for a check: a token for another server, the guest's or nobody's, or one out of date is
// believed for no one.
public sealed class CheckCallVerifier(
    IVerificationKeys agentKeys,
    ServerAddress? appsServer,
    TimeProvider clock,
    TokenVerificationSettings? settings = null)
{
    public const string NoAppsServer = "NoAppsServer";
    public const string NamesNoPerson = "NamesNoPerson";

    private readonly TokenVerifier _verifier = new(TokenIssuers.Agent, agentKeys, clock, settings);

    public async Task<TurnAdmission> VerifyAsync(string? token, CancellationToken ct)
    {
        if (appsServer is null)
        {
            return new TurnAdmission.Refused(NoAppsServer);
        }

        var verification = await _verifier.VerifyAsync(token, TokenAudiences.CallTo(appsServer), ct);
        if (!verification.IsVerified)
        {
            return new TurnAdmission.Refused(verification.Refusal.Value.ToString());
        }

        var claims = verification.Claims;
        return claims.Account is null
            ? new TurnAdmission.Refused(NamesNoPerson)
            : new TurnAdmission.Admitted(IdentityClaims.Person(claims.Subject, claims.TenantId, claims.ChannelId, claims.Roles));
    }
}