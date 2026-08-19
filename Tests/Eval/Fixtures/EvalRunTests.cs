using Domain.Extensions;
using Microsoft.Extensions.AI;
using Shouldly;
using Tests.Eval.Harness;

namespace Tests.Eval.Fixtures;

// The run's input list, without a model: scripted history has to reach the agent as ordinary
// prior messages — decorated like the channel's own, timestamped before the turn — or a
// multi-turn scenario would be testing a conversation the model never saw.
public class EvalRunTests
{
    private static Scenario TheScenario => new()
    {
        Name = "a scenario with history",
        AgentId = "jonas",
        Turn = new EvalTurn { Text = "resúmemelo en una frase", Sender = "fran" },
        Instant = new DateTimeOffset(2026, 8, 17, 20, 0, 0, TimeSpan.FromHours(2)),
        CallCeiling = 2,
        History =
        [
            new HistoryExchange("te cuento una cosa", "cuéntame"),
            new HistoryExchange("esta es la cosa", "anotado")
        ]
    };

    [Fact]
    public void Messages_CarryTheHistoryOldestFirst_ThenTheTurn()
    {
        var messages = EvalRun.Messages(TheScenario, "eval:test");

        messages.Count.ShouldBe(5);
        messages.Select(m => m.Role).ShouldBe(
            [ChatRole.User, ChatRole.Assistant, ChatRole.User, ChatRole.Assistant, ChatRole.User]);
        messages[0].Text.ShouldBe("te cuento una cosa");
        messages[3].Text.ShouldBe("anotado");
        messages[4].Text.ShouldBe("resúmemelo en una frase");
    }

    [Fact]
    public void HistoryUserMessages_AreDecoratedLikeTheTurn_AndTimestampedBeforeIt()
    {
        var messages = EvalRun.Messages(TheScenario, "eval:test");

        messages[0].GetSenderId().ShouldBe("fran");
        messages[0].GetTimestamp().ShouldBe(TheScenario.Instant.AddMinutes(-10));
        messages[2].GetTimestamp().ShouldBe(TheScenario.Instant.AddMinutes(-5));
        messages[4].GetTimestamp().ShouldBe(TheScenario.Instant);

        // Only the turn carries the conversation context and would carry the recall block: the
        // context is read once per run, and recall in production rides the current message.
        messages[4].GetConversationContext().ShouldNotBeNull();
        messages[0].GetConversationContext().ShouldBeNull();
    }

    [Fact]
    public void AScenarioWithoutHistory_IsTheOneTurnItAlwaysWas()
    {
        var single = EvalRun.Messages(TheScenario with { History = [] }, "eval:test");

        single.ShouldHaveSingleItem().Text.ShouldBe("resúmemelo en una frase");
    }
}