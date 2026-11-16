using Domain.Conversations;
using Domain.Extensions;
using Microsoft.Extensions.AI;
using Shouldly;

namespace Tests.Unit.Domain.Conversations;

public class HistoryEntriesTests
{
    [Fact]
    public void AStoredEntryId_IsTheMessagesName()
    {
        var message = new ChatMessage(ChatRole.User, "hola");
        message.SetEntryId("abc");

        HistoryEntries.IdAt([message], 0).ShouldBe("abc");
        HistoryEntries.IndexOf([message], "abc").ShouldBe(0);
    }

    // A message kept before entry ids has none, and is named by where it is and what it says: the same
    // name every read gives it, and one that stops answering once the message there changed.
    [Fact]
    public void AMessageWithNoEntryId_IsNamedByItsPlaceAndWhatItSays()
    {
        List<ChatMessage> list = [Message(ChatRole.User, "hola"), Message(ChatRole.Assistant, "buenas")];

        var id = HistoryEntries.IdAt(list, 1);

        id.ShouldStartWith("legacy:1:");
        HistoryEntries.IdAt(list, 1).ShouldBe(id);
        HistoryEntries.IndexOf(list, id).ShouldBe(1);
        HistoryEntries.IdAt(list, 0).ShouldNotBe(id);
    }

    [Fact]
    public void ALegacyName_WhoseMessageChanged_FindsNothing()
    {
        List<ChatMessage> list = [Message(ChatRole.User, "hola"), Message(ChatRole.Assistant, "buenas")];
        var id = HistoryEntries.IdAt(list, 1);

        List<ChatMessage> changed = [Message(ChatRole.User, "hola"), Message(ChatRole.Assistant, "otra cosa")];

        HistoryEntries.IndexOf(changed, id).ShouldBeNull();
        HistoryEntries.IndexOf([changed[0]], id).ShouldBeNull();
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("legacy:x:y")]
    [InlineData("legacy:7:abc")]
    public void AnUnknownName_FindsNothing(string id) =>
        HistoryEntries.IndexOf([Message(ChatRole.User, "hola")], id).ShouldBeNull();

    private static ChatMessage Message(ChatRole role, string text)
    {
        var message = new ChatMessage(role, text);
        message.SetTimestamp(new DateTimeOffset(2026, 11, 15, 10, 0, 0, TimeSpan.Zero));
        return message;
    }
}