using Shouldly;
using WebChat.Client.Components;
using WebChat.Client.Models;

namespace Tests.Unit.WebChat.Client.Components;

public class ChatInputLogicTests
{
    // Whether a reply is running is no part of it: a message sent then waits in the topic's queue.
    [Theory]
    [InlineData(false, "hello", true)]  // connected, has text -> can send
    [InlineData(true, "hello", false)]  // composer disabled (no agent / disconnected)
    [InlineData(false, "", false)]      // empty input
    [InlineData(false, "   ", false)]   // whitespace-only input
    [InlineData(false, null, false)]    // null input
    public void CanSend_ReturnsExpected(bool disabled, string? inputText, bool expected)
    {
        ChatInputLogic.CanSend(disabled, inputText).ShouldBe(expected);
    }

    // Read-only is said, not left to a refusal: the message would show as sent and then fail.
    [Fact]
    public void ReadOnlyNote_AnotherPersonsPrivateTopic_NamesWhoseItIs()
    {
        var note = ChatInputLogic.ReadOnlyNote(
            new StoredTopic { OwnerId = "leo", OwnerName = "Leo Ortega", Shared = false }, "marta");

        note.ShouldNotBeNull();
        note.ShouldContain("Leo Ortega");
    }

    [Fact]
    public void ReadOnlyNote_ATopicIMayWriteIn_IsNone()
    {
        ChatInputLogic.ReadOnlyNote(new StoredTopic { OwnerId = "leo", Shared = true }, "marta").ShouldBeNull();
        ChatInputLogic.ReadOnlyNote(null, "marta").ShouldBeNull();
    }
}