using Domain.DTOs;
using Domain.DTOs.Channel;
using Domain.Security;
using Shouldly;

namespace Tests.Unit.Domain.Security;

// Who a fire acts for, decided by the agent's own check of the delegation it carries, built here
// directly: the person the delegation names when the agent signed it for this very object, still doing
// what it did when their turn wrote it, and nobody — with the reason — otherwise. One row per answer.
public sealed class DelegationVerifierTests
{
    private static readonly Account _ana = new("0123456789abcdef0123456789abcdef", "fedcba9876543210fedcba9876543210");

    private static readonly DelegatedObject _nightly = DelegatedObject.Of(DelegatedObject.Schedule, "nightly",
        new DelegatedSchedule("jonas", "send me the news", "0 9 * * *", null, null));

    private static ChannelMessage Fire(
        string scheduleId = "nightly", string content = "send me the news", FireDelegation? delegation = null,
        MessageOriginKind kind = MessageOriginKind.Schedule) => new()
        {
            ConversationId = "conv-1",
            Content = content,
            Sender = "scheduler",
            ChannelId = "scheduling",
            AgentId = "jonas",
            ReplyTo = [new ReplyTarget(ChannelIds.SignalR, null)],
            Origin = new MessageOrigin(kind, scheduleId),
            Delegation = delegation ?? new FireDelegation(TestDelegations.For(_ana, ChannelIds.SignalR, _nightly), _nightly.Description)
        };

    private static Task<FireAdmission> AdmitAsync(ChannelMessage fire) =>
        TestDelegations.Verifier().VerifyFireAsync(fire, CancellationToken.None);

    [Fact]
    public async Task AFireDoingWhatItsDelegationSays_ActsForThePersonItNames()
    {
        var admission = await AdmitAsync(Fire());

        admission.ShouldBeOfType<FireAdmission.Delegated>().Identity.Account.ShouldBe(_ana);
    }

    public static TheoryData<string, string> Refusals => new()
    {
        { "another schedule's fire", DelegationVerifier.NotThisFire },
        { "a fire asking other words", DelegationVerifier.NotThisFire },
        { "a description changed since", DelegationVerifier.DigestMismatch },
        { "a watch's delegation on a schedule's fire", DelegationVerifier.WrongKind },
        { "a forged token", "UnknownKey" },
        { "a download's fire", DelegationVerifier.NotDelegable }
    };

    [Theory]
    [MemberData(nameof(Refusals))]
    public async Task AFireItsDelegationDoesNotCover_ActsForNobody_SayingWhy(string what, string reason)
    {
        var watch = DelegatedObject.Of(DelegatedObject.Watch, "nightly",
            new DelegatedWatch("jonas", ["send me the news"], null));
        var changed = _nightly with { Description = _nightly.Description.Replace("news", "weather") };
        var fire = what switch
        {
            "another schedule's fire" => Fire(scheduleId: "weekly"),
            "a fire asking other words" => Fire(content: "delete everything"),
            "a description changed since" => Fire(delegation: new FireDelegation(
                TestDelegations.For(_ana, ChannelIds.SignalR, _nightly), changed.Description)),
            "a watch's delegation on a schedule's fire" => Fire(delegation: new FireDelegation(
                TestDelegations.For(_ana, ChannelIds.SignalR, watch), _nightly.Description)),
            "a forged token" => Fire(delegation: new FireDelegation(
                TestDelegations.Forged(_ana, ChannelIds.SignalR, _nightly), _nightly.Description)),
            "a download's fire" => Fire(kind: MessageOriginKind.Download),
            _ => throw new ArgumentOutOfRangeException(nameof(what))
        };

        (await AdmitAsync(fire)).ShouldBe(new FireAdmission.Undelegated(reason), what);
    }

    [Fact]
    public async Task AFireCarryingNone_ActsForNobody_AndSaysNothing()
    {
        (await AdmitAsync(Fire() with { Delegation = null })).ShouldBe(new FireAdmission.Undelegated(null));
    }
}