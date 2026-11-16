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

    // A reply cut by a steer may have said nothing: reloaded, it shows the tools it ran where it would
    // have spoken, as the bubble showed them while it streamed. A reply with words keeps its words alone.
    [Fact]
    public void ToChatMessageModel_AReplyThatSaidNothing_ShowsTheToolsItRan()
    {
        new ChatHistoryMessage("m1", "assistant", "", null, null, ToolsCalled: ["web_search", "web_browse"])
            .ToChatMessageModel().ToolCalls.ShouldBe("web_search\nweb_browse");
        new ChatHistoryMessage("m2", "assistant", "Found it", null, null, ToolsCalled: ["web_search"])
            .ToChatMessageModel().ToolCalls.ShouldBeNull();
    }
}