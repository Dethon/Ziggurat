using Domain.DTOs.WebChat;
using Shouldly;
using WebChat.Client.Extensions;

namespace Tests.Unit.WebChat.Client.Extensions;

public class ChatHistoryMessageExtensionsTests
{
    // A failed turn's answer reloads as the error it was shown as, so it is drawn as one and the last
    // of them offers its retry.
    [Fact]
    public void ToChatMessageModel_AFailedTurnsAnswer_IsAnError()
    {
        var model = new ChatHistoryMessage("m1", "assistant", "OpenRouter refused your household's key.", null, null,
            IsError: true).ToChatMessageModel();

        model.IsError.ShouldBeTrue();
    }

    [Fact]
    public void ToChatMessageModel_CarriesTheResponseFormat() =>
        new ChatHistoryMessage("m1", "assistant", "{}", null, null, ResponseFormat: "meal_estimate").ToChatMessageModel()
            .ResponseFormat.ShouldBe("meal_estimate");

    // What a turn did is kept with the reply it led to, so a reload draws its reasoning and its calls.
    [Fact]
    public void ToChatMessageModel_CarriesWhatItsTurnDid()
    {
        var call = new ToolCallStep { CallId = "c1", Name = "web_search", Arguments = "{}", Result = "ok", Outcome = ToolCallOutcome.Done };
        var model = new ChatHistoryMessage("m1", "assistant", "", null, null,
                Steps: [new TurnStep(Reasoning: "think")], StepsAfter: [new TurnStep(Tool: call)])
            .ToChatMessageModel();

        model.Steps!.Single().Reasoning.ShouldBe("think");
        model.StepsAfter!.Single().Tool.ShouldBe(call);
        model.HasContent.ShouldBeTrue();
    }
}