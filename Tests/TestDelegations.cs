using Domain.DTOs;
using Domain.DTOs.Channel;
using Domain.Security;
using Domain.Security.Signing;

namespace Tests;

// Delegations as the agent signs them when a person's turn writes something a later turn acts on,
// over the suite's agent key, and the agent's own check of them at fire time, with the delivery
// default every test server reads (Domain/delivery.json). What a test hands a monitor to believe a
// fire, and what it stores on an object to stand for a write it did not make through the agent.
internal static class TestDelegations
{
    public static readonly DeliverySettings Delivery = new() { DefaultDeliverTo = [ChannelIds.SignalR] };

    public static DelegationVerifier Verifier(TimeProvider? clock = null, DeliverySettings? delivery = null) =>
        new(TestSigningKeys.Store(TestSigningKeys.Agent), clock ?? TimeProvider.System, delivery ?? Delivery);

    // What the scheduler hands the agent with a schedule's fire: the delegation the agent signed for
    // the person who wrote it, beside what the schedule does.
    public static FireDelegation OfSchedule(Account person, string channel, Schedule schedule)
    {
        var what = Domain.Tools.Scheduling.Vfs.ScheduleDelegation.Of(schedule);
        return new FireDelegation(For(person, channel, what), what.Description);
    }

    public static string For(Account person, string channel, DelegatedObject what) =>
        McpTestSecret.CallTokens.Delegate(
            IdentityClaims.Person(person.UserId, person.TenantId, channel, ["member"]), what);

    // A fire as the agent's channel connection reads it off the wire, checked the way the conversation
    // group checks it as it arrives.
    public static Task<FireAdmission> AdmitAsync(ChannelMessageNotification fire, string channel) =>
        Verifier().VerifyFireAsync(new ChannelMessage
        {
            ConversationId = fire.ConversationId,
            Content = fire.Content,
            Sender = fire.Sender,
            ChannelId = channel,
            AgentId = fire.AgentId,
            UserId = fire.UserId,
            Principal = fire.Principal,
            Delegation = fire.Delegation,
            ReplyTo = fire.ReplyTo,
            Origin = fire.Origin
        }, CancellationToken.None);

    // The same claims signed by a key the agent does not hold: what a server would have to make up.
    public static string Forged(Account person, string channel, DelegatedObject what) =>
        new TokenSigner(TestSigningKeys.Store(TestSigningKeys.Accounts)).Sign((Delegation)new Delegation(
                IdentityClaims.Person(person.UserId, person.TenantId, channel, ["member"]), what)
            .Stamped(TokenIssuers.Agent, TokenAudiences.Delegation, DateTimeOffset.UtcNow, Delegation.ObjectLifetime));
}