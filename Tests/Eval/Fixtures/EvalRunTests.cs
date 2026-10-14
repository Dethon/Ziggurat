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
// A channel's turn, as ConversationGroup builds it from the channel's message: the format the
// answer must take and the attachments as references the channel still holds, never bytes.
public class EvalRunChannelTurnTests
{
    private static Scenario Formatted(params EvalAttachment[] attachments) => new()
    {
        Name = "a formatted turn with photos",
        AgentId = "sugary",
        Turn = new EvalTurn
        {
            Text = "One photo of the meal is attached.",
            Sender = "sugary-app",
            Channel = "sugary",
            ResponseFormat = Domain.Tools.Sugary.Estimation.MealEstimateSchema.Format,
            Attachments = attachments
        },
        Instant = new DateTimeOffset(2026, 10, 15, 14, 0, 0, TimeSpan.FromHours(2)),
        CallCeiling = 1
    };

    [Fact]
    public void Turn_CarriesTheFormat_AndTheAttachmentsAsReferencesOfItsChannel()
    {
        var photo = Path.GetTempFileName();
        File.WriteAllBytes(photo, [1, 2, 3, 4, 5]);
        try
        {
            var turn = EvalRun.Turn(Formatted(new EvalAttachment(photo, "image/jpeg")), "eval:test");

            turn.GetResponseFormat()!.Name.ShouldBe("meal_estimate");
            var reference = turn.GetAttachments().ShouldHaveSingleItem();
            reference.Id.ShouldBe(EvalAttachments.References(Formatted(new EvalAttachment(photo, "image/jpeg")).Turn)[0].Id);
            reference.FileName.ShouldBe(Path.GetFileName(photo));
            reference.MediaType.ShouldBe("image/jpeg");
            reference.SizeBytes.ShouldBe(5);
            turn.GetAttachmentChannelId().ShouldBe("sugary");
            turn.Contents.ShouldAllBe(content => content is TextContent);
            turn.GetConversationContext()!.Origin.ChannelId.ShouldBe("sugary");
        }
        finally
        {
            File.Delete(photo);
        }
    }

    [Fact]
    public void ATurnWithNothingAttached_NamesNoChannelAndNoFormat()
    {
        var plain = Formatted() with { Turn = Formatted().Turn with { ResponseFormat = null } };

        var turn = EvalRun.Turn(plain, "eval:test");

        turn.GetAttachments().ShouldBeNull();
        turn.GetAttachmentChannelId().ShouldBeNull();
        turn.GetResponseFormat().ShouldBeNull();
    }

    // The channel's own rule: the reply is the last message that said anything. A turn that
    // writes a line, calls a tool and then answers sends two messages, and only the last is read.
    [Fact]
    public void Answer_IsTheLastAssistantMessageWithText()
    {
        var messages = new List<ChatMessage>
        {
            new(ChatRole.Assistant, "let me look"),
            new(ChatRole.Assistant, [new FunctionCallContent("1", "domain__filesystem__file_read")]),
            new(ChatRole.Tool, [new FunctionResultContent("1", "ok")]),
            new(ChatRole.Assistant, "{\"items\":[]}"),
            new(ChatRole.Assistant, [new FunctionCallContent("2", "x")])
        };

        EvalRun.Answer(messages).ShouldBe("{\"items\":[]}");
        EvalRun.Answer([new ChatMessage(ChatRole.Tool, [new FunctionResultContent("1", "ok")])]).ShouldBeNull();
    }
}