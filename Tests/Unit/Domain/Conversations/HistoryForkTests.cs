using Domain.Conversations;
using Domain.Extensions;
using Microsoft.Extensions.AI;
using Shouldly;

namespace Tests.Unit.Domain.Conversations;

// What a fork copies: the line up to the message it was asked from — through the end of a reply's
// turn, or up to a person's message, whose words go back to the composer to be sent differently.
public class HistoryForkTests
{
    private static readonly IReadOnlyList<ChatMessage> Line =
    [
        Message("u1", ChatRole.User), Message("call", ChatRole.Assistant), Message("result", ChatRole.Tool),
        Message("a1", ChatRole.Assistant), Message("u2", ChatRole.User), Message("a2", ChatRole.Assistant)
    ];

    [Fact]
    public void FromAReply_CopiesThroughTheEndOfItsTurn()
    {
        var fork = HistoryFork.Of(Line, "call").ShouldNotBeNull();

        fork.Messages.Select(m => m.Text).ShouldBe(["u1", "call", "result", "a1"]);
        fork.ComposerText.ShouldBeNull();
    }

    [Fact]
    public void FromAPersonsMessage_CopiesWhatCameBefore_AndHandsItsWordsBack()
    {
        var fork = HistoryFork.Of(Line, "u2").ShouldNotBeNull();

        fork.Messages.Select(m => m.Text).ShouldBe(["u1", "call", "result", "a1"]);
        fork.ComposerText.ShouldBe("u2");
    }

    // The copy is a conversation of its own: its messages are named afresh, and the originals untouched.
    [Fact]
    public void TheCopy_IsNamedAfresh()
    {
        var fork = HistoryFork.Of(Line, "a2").ShouldNotBeNull();

        fork.Messages.Select(m => m.GetEntryId()).ShouldAllBe(id => id != null && !Line.Any(o => o.GetEntryId() == id));
        Line[0].GetEntryId().ShouldBe("u1");
    }

    [Fact]
    public void AMessageNotOnTheLine_ForksNothing() => HistoryFork.Of(Line, "gone").ShouldBeNull();

    private static ChatMessage Message(string id, ChatRole role)
    {
        var message = new ChatMessage(role, id);
        message.SetEntryId(id);
        return message;
    }
}