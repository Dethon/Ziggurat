using Domain.Channels;
using Domain.DTOs;
using Domain.DTOs.Channel;
using Domain.Security.Signing;

namespace Domain.Security;

// Who a fire acts for: the person a delegation names, when the agent itself signed it, for the very
// object this fire comes from, still doing what it did when that person's turn wrote it — and nobody
// otherwise. A server keeps delegations and hands them back; it cannot make one, cannot move one onto
// another object, and cannot fire an object it changed as the person who wrote it.
//
// What the fire does is checked against the description it carries as far as the agent can see it:
// the agent that runs, the words it runs (a schedule's; a watch's are rendered by the home and arrive
// as the home rendered them), the conversation it wakes (a login handoff's) and where the answer goes,
// resolved against the same default delivery the servers use. When the fire should have come is the
// server's: a digest covers a schedule's timing, but nothing in a fire says when it was due.
public sealed class DelegationVerifier(
    IVerificationKeys agentKeys,
    TimeProvider clock,
    DeliverySettings delivery,
    TokenVerificationSettings? settings = null)
{
    public const string NotDelegable = "NotDelegable";
    public const string NamesNoPerson = "NamesNoPerson";
    public const string WrongKind = "WrongKind";
    public const string DigestMismatch = "DigestMismatch";
    public const string NotThisFire = "NotThisFire";

    private readonly TokenVerifier _verifier = new(TokenIssuers.Agent, agentKeys, clock, settings);

    public async Task<FireAdmission> VerifyFireAsync(ChannelMessage fire, CancellationToken ct)
    {
        if (fire.Delegation is not { } carried)
        {
            return new FireAdmission.Undelegated(null);
        }

        // A download's fire, or anything else that raises no delegable object, acts for nobody
        // whatever it carries.
        if (fire.Origin is not { } origin || DelegatedObject.KindOf(origin.Kind) is not { } kind)
        {
            return new FireAdmission.Undelegated(NotDelegable);
        }

        var verification = await _verifier.VerifyAsync<Delegation>(carried.Token, TokenAudiences.Delegation, ct);
        if (!verification.IsVerified)
        {
            return new FireAdmission.Undelegated(verification.Refusal.Value.ToString());
        }

        var delegation = verification.Claims;
        string? refusal = delegation switch
        {
            { Account: null } => NamesNoPerson,
            _ when delegation.Kind != kind => WrongKind,
            _ when DelegatedObject.DigestOf(carried.Object) != delegation.Digest => DigestMismatch,
            _ when !DoesWhatItSays(fire, origin, kind, delegation.ObjectId, carried.Object) => NotThisFire,
            _ => null
        };
        return refusal is not null
            ? new FireAdmission.Undelegated(refusal)
            : new FireAdmission.Delegated(IdentityClaims.Person(
                delegation.Subject, delegation.TenantId, delegation.ChannelId, delegation.Roles));
    }

    private bool DoesWhatItSays(ChannelMessage fire, MessageOrigin origin, string kind, string objectId, string description) =>
        kind switch
        {
            DelegatedObject.Schedule =>
                origin.ScheduleId == objectId
                && DelegatedObject.Read<DelegatedSchedule>(description) is { } schedule
                && schedule.AgentId == fire.AgentId
                && schedule.Prompt == fire.Content
                && SameTargets(fire.ReplyTo, FirePlanning.ReplyTargets(schedule.DeliverTo, delivery.DefaultDeliverTo)),
            DelegatedObject.Watch =>
                origin.WatchId == objectId
                && DelegatedObject.Read<DelegatedWatch>(description) is { Prompts.Count: > 0 } watch
                && SameTargets(fire.ReplyTo, FirePlanning.ReplyTargets(watch.DeliverTo, delivery.DefaultDeliverTo)),
            DelegatedObject.Login =>
                DelegatedObject.Read<DelegatedLogin>(description) is { } login
                && login.ConversationId == objectId
                && login.AgentId == fire.AgentId
                // Done wakes the conversation that asked, answering where it would have; a turn that
                // could not show the link put it in a new conversation of the person's chat client,
                // which only their own sign-in opens.
                && ((fire.ConversationId == login.ConversationId && SameTargets(fire.ReplyTo, [login.Origin]))
                    || SameTargets(fire.ReplyTo, [new ReplyTarget(ChannelIds.SignalR, null)])),
            _ => false
        };

    private static bool SameTargets(IReadOnlyList<ReplyTarget>? fired, IReadOnlyList<ReplyTarget> described) =>
        (fired ?? []).SequenceEqual(described);
}

// Who a fire acts for: the person its delegation verified as, or nobody — with the reason a
// delegation it carried was not believed, null when it carried none.
public abstract record FireAdmission
{
    public sealed record Delegated(IdentityClaims Identity) : FireAdmission;

    public sealed record Undelegated(string? Refusal) : FireAdmission;
}