using Domain.DTOs.Channel;
using Domain.Judgments;
using Domain.Memory;
using Shouldly;

namespace Tests.Unit.Domain.Judgments;

// A judgment is billed to the account it was asked for, so a person's Jev figures join their turns'
// under one id whatever name either went by.
public class JudgmentCallerTests
{
    [Fact]
    public void ASharedServersCall_IsBilledToTheAccountItsTurnActsFor()
    {
        var turn = new ConversationContext(
            "jonas", "1:2", "acc-alice", new ReplyTarget("signalr", "1:2"),
            Principal: new Principal("signalr", "acc-alice", new Account("home", "acc-alice")));

        JudgmentCaller.For(turn).UserId.ShouldBe("acc-alice");
        JudgmentCaller.For(turn with { Principal = null }).UserId.ShouldBeNull();
    }

    [Fact]
    public void AMemoryJudgment_IsBilledToTheAccountItIsAbout()
    {
        new MemoryJudgmentContext("acc-alice", "jonas").Caller.UserId.ShouldBe("acc-alice");
    }
}