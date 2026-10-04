using Domain.DTOs.Channel;
using Domain.Judgments;
using Shouldly;

namespace Tests.Unit.Judgments;

// A server's tool call knows its turn only by the context the agent stamps on its `_meta`; the
// judgment it asks carries all of it, so the judge's client can both keep a local turn local and
// bill the rest to whoever asked.
public class JudgmentCallerTests
{
    [Fact]
    public void For_AConversation_CarriesItsModelAndWhoAsked()
    {
        var conversation = new ConversationContext(
            "jonas", "conv-7", "fran", new ReplyTarget("signalr", "conv-7"), ConfigPatchModel: "lemonade/qwen3");

        JudgmentCaller.For(conversation).ShouldBe(new JudgmentCaller("lemonade/qwen3", "fran", "jonas", "conv-7"));
    }

    [Fact]
    public void For_NoConversation_IsNobody() =>
        JudgmentCaller.For(null).ShouldBe(JudgmentCaller.None);
}