using Accounts.Internal;
using Accounts.Settings;
using Domain.DTOs.Household;
using Domain.Security;
using Domain.Security.Signing;

namespace Accounts.Configuration;

public abstract record HouseholdProven
{
    public sealed record Household(string Id) : HouseholdProven;

    public sealed record Refused(int Status, HouseholdConfigRefused Refusal) : HouseholdProven;
}

// Which household a question about configuration is for, from something signed and never from a
// parameter a caller picks: a shared server's credential alone must not open another household's
// section. A shared server forwards what it was handed — the agent's call token for it, which must be
// signed for that server, or this service's assertion of the person it serves, which must name that
// server's channel — and the household is the one the signed claims carry. The agent names the
// household itself (it signs every call token, so there is nothing it could prove that it did not
// say), and proves only that it is the agent, with a token of its own for the configuration: its
// credential here is one SexyTime's server holds too.
public sealed class HouseholdProofs
{
    private readonly TokenVerifier _agent;
    private readonly TokenVerifier _accounts;

    public HouseholdProofs(AccountsSettings settings, SigningKeyStore ownKeys, AccountsClock clock, IHttpClientFactory http)
    {
        var policy = settings.Mcp.TokenVerification;
        // The agent signs on the wall clock, so its tokens are checked on it, never on the service's
        // own clock, which a test moves; this service's assertions are dated by its own.
        _agent = new TokenVerifier(
            TokenIssuers.Agent,
            new JwksCache(ct => http.CreateClient(nameof(HouseholdProofs)).GetStringAsync(settings.AgentKeysUrl, ct),
                TimeProvider.System, policy),
            TimeProvider.System,
            policy);
        _accounts = new TokenVerifier(TokenIssuers.Accounts, ownKeys, clock.Time, policy);
    }

    public async Task<HouseholdProven> AgentAsync(HouseholdConfigQuery query, CancellationToken ct)
    {
        if (query is not { HouseholdId.Length: > 0, Assertion: null })
        {
            return BadQuery("the agent names the household and presents its own token for the configuration, and nothing else");
        }

        var verified = await _agent.VerifyAsync(query.CallToken, TokenAudiences.HouseholdConfiguration, ct);
        return verified.IsVerified
            ? new HouseholdProven.Household(query.HouseholdId)
            : Unproven($"the agent's token for the configuration is {verified.Refusal}");
    }

    // The agent asking about every household at once (dreaming's walk) names none, and proves only that
    // it is the agent, as for its own view. Null when it did.
    public async Task<HouseholdProven.Refused?> AgentItselfAsync(HouseholdConfigQuery query, CancellationToken ct)
    {
        if (query is not { HouseholdId: null, Assertion: null, Delegation: null })
        {
            return (HouseholdProven.Refused)BadQuery("asked about every household, the agent names none and presents its own token alone");
        }

        var verified = await _agent.VerifyAsync(query.CallToken, TokenAudiences.HouseholdConfiguration, ct);
        return verified.IsVerified
            ? null
            : (HouseholdProven.Refused)Unproven($"the agent's token for the configuration is {verified.Refusal}");
    }

    // `callers` are the names the presented credential matched that may read the view asked for.
    public async Task<HouseholdProven> SharedAsync(HouseholdConfigQuery query, IReadOnlyList<string> callers, CancellationToken ct)
    {
        if (query.HouseholdId is not null)
        {
            return BadQuery("a shared server never names a household: the call token or assertion it holds does");
        }

        if (new[] { query.CallToken, query.Assertion, query.Delegation }.Count(proof => proof is not null) != 1
            || query is { CallToken.Length: 0 } or { Assertion.Length: 0 } or { Delegation.Length: 0 })
        {
            return BadQuery("a shared server presents a call token, an assertion or a schedule's delegation, exactly one");
        }

        if (query.Delegation is not null && !callers.Contains(InternalCallers.Scheduling))
        {
            return BadQuery("only the scheduling server proves a household with a schedule's delegation");
        }

        var (claims, refusal) = query switch
        {
            { CallToken: { } token } => await CallTokenAsync(token, callers, ct),
            { Assertion: { } assertion } => await AssertionAsync(assertion, callers, ct),
            _ => await DelegationAsync(query.Delegation!, ct)
        };
        return claims switch
        {
            null => Unproven(refusal ?? "the proof does not verify"),
            { IsNobody: true } or { TenantId.Length: 0 } => Unproven("the proof names nobody, so no household"),
            _ => new HouseholdProven.Household(claims.TenantId)
        };
    }

    // Signed for the server asking: a token the agent signed for another server is that server's.
    private async Task<(IdentityClaims? Claims, string? Refusal)> CallTokenAsync(string token, IReadOnlyList<string> callers, CancellationToken ct)
    {
        var refusal = TokenRefusal.WrongAudience;
        // A loop for the early return: the first of the asker's names the token was signed for is it.
        foreach (var caller in callers)
        {
            var verified = await _agent.VerifyAsync(token, TokenAudiences.CallTo(ServerAddress.Named(caller)), ct);
            if (verified.IsVerified)
            {
                return (verified.Claims, null);
            }

            refusal = verified.Refusal.Value;
        }

        return (null, $"the call token is {refusal}");
    }

    // The agent signs one for whatever a person's turn writes; only a schedule's names a schedule's
    // household, so a watch's or a login's handed on by the scheduling server proves nothing here.
    private async Task<(IdentityClaims? Claims, string? Refusal)> DelegationAsync(string delegation, CancellationToken ct)
    {
        var verified = await _agent.VerifyAsync<Delegation>(delegation, TokenAudiences.Delegation, ct);
        return verified switch
        {
            { IsVerified: false } => (null, $"the delegation is {verified.Refusal}"),
            { Claims.Kind: DelegatedObject.Schedule } => (verified.Claims, null),
            _ => (null, $"the delegation is for a {verified.Claims.Kind}, not a schedule")
        };
    }

    private async Task<(IdentityClaims? Claims, string? Refusal)> AssertionAsync(string assertion, IReadOnlyList<string> callers, CancellationToken ct)
    {
        var verified = await _accounts.VerifyAsync(assertion, TokenAudiences.Assertion, ct);
        if (!verified.IsVerified)
        {
            return (null, $"the assertion is {verified.Refusal}");
        }

        return callers.Any(caller => InternalCallers.AssertionChannelOf(caller) == verified.Claims.ChannelId)
            ? (verified.Claims, null)
            : (null, $"the assertion is for the {verified.Claims.ChannelId} channel, which is not the asker's");
    }

    private static HouseholdProven BadQuery(string detail) =>
        new HouseholdProven.Refused(StatusCodes.Status400BadRequest, new HouseholdConfigRefused(HouseholdConfigRefusal.BadQuery, detail));

    private static HouseholdProven Unproven(string detail) =>
        new HouseholdProven.Refused(StatusCodes.Status404NotFound, new HouseholdConfigRefused(HouseholdConfigRefusal.Unproven, detail));
}